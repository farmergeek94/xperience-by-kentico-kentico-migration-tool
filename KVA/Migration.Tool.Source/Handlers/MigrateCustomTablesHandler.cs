using System.Collections.Immutable;
using System.Diagnostics;
using System.Xml.Linq;

using CMS.DataEngine;
using CMS.FormEngine;
using CMS.Modules;
using CMS.ContentEngine.Internal;

using MediatR;

using Microsoft.Extensions.Logging;

using Migration.Tool.Common;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.Common.Helpers;
using Migration.Tool.Common.MigrationProtocol;
using Migration.Tool.Common.Services.BulkCopy;
using Migration.Tool.KXP.Api;
using Migration.Tool.Source.Contexts;
using Migration.Tool.Source.Helpers;
using Migration.Tool.Source.Model;
using Microsoft.Data.SqlClient;
using CMS.ContentEngine;
using CMS.Membership;

namespace Migration.Tool.Source.Handlers;

public class MigrateCustomTablesHandler(
    ILogger<MigrateCustomTablesHandler> logger,
    ModelFacade modelFacade,
    KxpClassFacade kxpClassFacade,
    IProtocol protocol,
    BulkDataCopyService bulkDataCopyService,
    IEntityMapper<ICmsClass, DataClassInfo> dataClassMapper,
    PrimaryKeyMappingContext primaryKeyMappingContext,
    ToolConfiguration configuration,
    KxpApiInitializer kxpApiInitializer
// ReusableSchemaService reusableSchemaService
)
    : IRequestHandler<MigrateCustomTablesCommand, CommandResult>
{
    private readonly Guid resourceGuidNamespace = new("C4E3F5FD-9220-4300-91CE-8EB565D3235E");
    private ResourceInfo? customTableResource;

    public async Task<CommandResult> Handle(MigrateCustomTablesCommand request, CancellationToken cancellationToken)
    {
        await MigrateCustomTables();

        return new GenericCommandResult();
    }

    private async Task<ResourceInfo> EnsureCustomTablesResource()
    {
        if (customTableResource != null)
        {
            return customTableResource;
        }

        const string resourceName = "customtables";
        var resourceGuid = GuidV5.NewNameBased(resourceGuidNamespace, resourceName);
        var resourceInfo = await ResourceInfoProvider.ProviderObject.GetAsync(resourceGuid);
        if (resourceInfo == null)
        {
            resourceInfo = new ResourceInfo
            {
                ResourceDisplayName = "Custom tables",
                ResourceName = resourceName,
                ResourceDescription = "Container resource for migrated custom tables",
                ResourceGUID = resourceGuid,
                ResourceLastModified = default,
                ResourceIsInDevelopment = false
            };
            ResourceInfoProvider.ProviderObject.Set(resourceInfo);
        }

        customTableResource = resourceInfo;
        return resourceInfo;
    }

    private async Task MigrateCustomTables()
    {
        if (!kxpApiInitializer.EnsureApiIsInitialized())
        {
            throw new InvalidOperationException("Falied to initialize kentico API. Please check configuration.");
        }

        using var srcClassesDe = EnumerableHelper.CreateDeferrableItemWrapper(
            modelFacade.Select<ICmsClass>("ClassIsCustomTable=1", "ClassID ASC")
        );

        var userInfoProvider = CMS.Core.Service.Resolve<IUserInfoProvider>();

        var contentItemManagerFactory = CMS.Core.Service.Resolve<IContentItemManagerFactory>();

        var user = userInfoProvider.Get("administrator");

        var contentItemManager = contentItemManagerFactory.Create(user.UserID);

        while (srcClassesDe.GetNext(out var di))
        {
            var (_, srcClass) = di;

            if (!srcClass.ClassIsCustomTable)
            {
                continue;
            }

            if (srcClass.ClassInheritsFromClassID is { } classInheritsFromClassId && !primaryKeyMappingContext.HasMapping<ICmsClass>(c => c.ClassID, classInheritsFromClassId))
            {
                // defer migration to later stage
                if (srcClassesDe.TryDeferItem(di))
                {
                    logger.LogTrace("Class {Class} inheritance parent not found, deferring migration to end. Attempt {Attempt}", Printer.GetEntityIdentityPrint(srcClass), di.Recurrence);
                }
                else
                {
                    logger.LogErrorMissingDependency(srcClass, nameof(srcClass.ClassInheritsFromClassID), srcClass.ClassInheritsFromClassID, typeof(DataClassInfo));
                    protocol.Append(HandbookReferences
                        .MissingRequiredDependency<ICmsClass>(nameof(ICmsClass.ClassID), classInheritsFromClassId)
                        .NeedsManualAction()
                    );
                }

                continue;
            }

            protocol.FetchedSource(srcClass);

            var xbkDataClass = kxpClassFacade.GetClass(srcClass.ClassGUID);

            protocol.FetchedTarget(xbkDataClass);

            if (await SaveClassUsingKxoApi(srcClass, xbkDataClass) is { } savedDataClass)
            {
                Debug.Assert(savedDataClass.ClassID != 0, "xbkDataClass.ClassID != 0");
                // MigrateClassSiteMappings(kx13Class, xbkDataClass);

                xbkDataClass = DataClassInfoProvider.ProviderObject.Get(savedDataClass.ClassID);
                // await MigrateAlternativeForms(srcClass, savedDataClass, cancellationToken);

                #region Migrate coupled data class data

                if (srcClass.ClassShowAsSystemTable is false)
                {
                    Debug.Assert(xbkDataClass.ClassTableName != null, "kx13Class.ClassTableName != null");
                    // var csi = new ClassStructureInfo(kx13Class.ClassXmlSchema, kx13Class.ClassXmlSchema, kx13Class.ClassTableName);

                    XNamespace nsSchema = "http://www.w3.org/2001/XMLSchema";
                    XNamespace msSchema = "urn:schemas-microsoft-com:xml-msdata";
                    var xDoc = XDocument.Parse(xbkDataClass.ClassXmlSchema);
                    var autoIncrementColumns = xDoc.Descendants(nsSchema + "element")
                        .Where(x => x.Attribute(msSchema + "AutoIncrement")?.Value == "true")
                        .Select(x => x.Attribute("name")?.Value).ToImmutableHashSet();

                    Debug.Assert(autoIncrementColumns.Count == 1, "autoIncrementColumns.Count == 1");
                    var r = (xbkDataClass.ClassTableName, xbkDataClass.ClassGUID, autoIncrementColumns);
                    logger.LogTrace("Class '{ClassGuild}' Resolved as: {Result}", srcClass.ClassGUID, r);


                    try
                    {
                        // check if data is present in target tables
                        if (bulkDataCopyService.CheckIfDataExistsInTargetTable(xbkDataClass.ClassTableName))
                        {
                            logger.LogWarning("Data exists in target coupled data table '{TableName}' - cannot migrate, skipping data migration", r.ClassTableName);
                            protocol.Append(HandbookReferences.DataMustNotExistInTargetInstanceTable(xbkDataClass.ClassTableName));
                            continue;
                        }

                        // get all the data for type
                        var data = Data(xbkDataClass.ClassTableName);

                        string languageCode = "en_US";

                        // create content items from data
                        foreach (var item in data)
                        {
                            var guid = Guid.NewGuid();
                            string nameColumn = item.Keys.Where(x => x.IndexOf("name", StringComparison.CurrentCultureIgnoreCase) > -1).FirstOrDefault("");
                            string safeNodeName = item[nameColumn]?.ToString() ?? guid.ToString();
                            CreateContentItemParameters createParams = new(
                                                                xbkDataClass.ClassName,
                                                                safeNodeName,
                                                                languageCode);
                            ContentItemData itemData = new(item);

                            int id = await contentItemManager.Create(createParams, itemData);
                            await contentItemManager.TryPublish(id, languageCode);

                        }

                        logger.LogInformation("Data imported for Custom Table: {ClassName}", xbkDataClass.ClassName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error while copying data to table");
                    }
                }

                #endregion
            }
        }
    }

    private async Task<DataClassInfo?> SaveClassUsingKxoApi(ICmsClass srcClass, DataClassInfo kxoDataClass)
    {
        var mapped = dataClassMapper.Map(srcClass, kxoDataClass);
        protocol.MappedTarget(mapped);

        try
        {
            if (mapped is { Success: true } result)
            {
                (var dataClassInfo, bool newInstance) = result;

                ArgumentNullException.ThrowIfNull(dataClassInfo, nameof(dataClassInfo));

                // if (reusableSchemaService.IsConversionToReusableFieldSchemaRequested(dataClassInfo.ClassName))
                // {
                //     dataClassInfo = reusableSchemaService.ConvertToReusableSchema(dataClassInfo);
                // }

                var dataFormInfo = new FormInfo(dataClassInfo.ClassFormDefinition);

                string[] columnNamesToRemove = ["ItemID", "ItemModifiedWhen", "ItemGUID"];

                dataFormInfo.RemoveFields((x) => columnNamesToRemove.Contains(x.Name));

                dataClassInfo.ClassFormDefinition = dataFormInfo.GetXmlDefinition();

                var formInfo = FormHelper.GetBasicFormDefinition("ContentItemDataID");

                var formItem = new FormFieldInfo
                {
                    Name = nameof(ContentItemDataInfo.ContentItemDataCommonDataID),
                    ReferenceToObjectType = ContentItemCommonDataInfo.OBJECT_TYPE,
                    ReferenceType = ObjectDependencyEnum.Required,
                    System = true,
                    DataType = "integer",
                    Enabled = true,
                    Visible = false
                };
                formInfo.AddFormItem(formItem);

                formItem = new FormFieldInfo
                {
                    Name = nameof(ContentItemDataInfo.ContentItemDataGUID),
                    IsUnique = true,
                    System = true,
                    DataType = "guid",
                    Enabled = true,
                    Visible = false
                };
                formInfo.AddFormItem(formItem);



                //var containerResource = await EnsureCustomTablesResource();
                //dataClassInfo.ClassResourceID = containerResource.ResourceID;

                // Remove the resource
                dataClassInfo.SetValue("ClassResourceID", null);

                // Set the content hub type
                dataClassInfo.ClassType = ClassType.CONTENT_TYPE;
                dataClassInfo.ClassContentTypeType = ClassContentTypeType.REUSABLE;
                dataClassInfo.ClassShortName = dataClassInfo.ClassName.Replace(".", "");

                // Reset to reusable content type. 
                SetFormDefinition(dataClassInfo, formInfo);

                kxpClassFacade.SetClass(dataClassInfo);

                protocol.Success(srcClass, dataClassInfo, mapped);
                logger.LogEntitySetAction(newInstance, dataClassInfo);

                primaryKeyMappingContext.SetMapping<ICmsClass>(
                    r => r.ClassID,
                    srcClass.ClassID,
                    dataClassInfo.ClassID
                );

                return dataClassInfo;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while saving page type {ClassName}", srcClass.ClassName);
        }

        return null;
    }

    /// <summary>
    /// Ensure that the form is upserted with any existing form
    /// </summary>
    /// <param name="info"></param>
    /// <param name="form"></param>
    private void SetFormDefinition(DataClassInfo info, FormInfo form)
    {
        if (info.ClassID > 0)
        {
            var existingForm = new FormInfo(info.ClassFormDefinition);
            existingForm.CombineWithForm(form, new());
            info.ClassFormDefinition = existingForm.GetXmlDefinition();
        }
        else
        {
            info.ClassFormDefinition = form.GetXmlDefinition();
        }
    }

    private IEnumerable<Dictionary<string, object>> Data(string tableName)
    {
        using SqlConnection conn = new(configuration.KxConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {tableName}";
        using var reader = cmd.ExecuteReader();
        string[] columnNamesToRemove = ["ItemID", "ItemModifiedWhen", "ItemGUID"];

        while (reader.Read())
        {
            var dict = new Dictionary<string, object>();
            for (int lp = 0; lp < reader.FieldCount; lp++)
            {
                string name = reader.GetName(lp);

                if (!columnNamesToRemove.Contains(name))
                {
                    dict.Add(reader.GetName(lp), reader.GetValue(lp));
                }
            }
            yield return dict;
        }
    }
}
