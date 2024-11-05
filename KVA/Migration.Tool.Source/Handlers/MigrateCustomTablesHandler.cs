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
using Migration.Tool.Source.Providers;
using Migration.Tool.Common.Enumerations;
using CMS.MediaLibrary;
using Migration.Tool.Source.Services;
using Migration.Tool.Source.Auxiliary;

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
    KxpApiInitializer kxpApiInitializer,
    IAttachmentMigrator attachmentMigrator,
    MediaLinkServiceFactory mediaLinkServiceFactory,
    EntityIdentityFacade entityIdentityFacade,
    KxpMediaFileFacade mediaFileFacade,
    IAssetFacade assetFacade
// ReusableSchemaService reusableSchemaService
)
    : IRequestHandler<MigrateCustomTablesCommand, CommandResult>
{
    private readonly Guid resourceGuidNamespace = new("C4E3F5FD-9220-4300-91CE-8EB565D3235E");
    private ResourceInfo? customTableResource;

    private readonly ContentItemNameProvider contentItemNameProvider = new(new ContentItemNameValidator());


    public async Task<CommandResult> Handle(MigrateCustomTablesCommand request, CancellationToken cancellationToken)
    {
        await MigrateCustomTables();

        return new GenericCommandResult();
    }

    private readonly string[] columnNamesToRemove = ["ItemID", "ItemModifiedWhen", "ItemModifiedBy", "ItemGUID", "ItemCreatedBy", "ItemCreatedWhen", "ItemOrder"];

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

        var sites = modelFacade.SelectAll<ICmsSite>();
        foreach (var ksSite in sites)
        {

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
                            var data = Data(srcClass, ksSite);

                            string languageCode = ContentLanguageInfoProvider.ProviderObject.Get().ToList().Select(x => x.ContentLanguageName).FirstOrDefault() ?? "en";

                            // create content items from data
                            await foreach (var item in data)
                            {
                                var guid = Guid.NewGuid();
                                string nameColumn = item.Keys.OrderByDescending(x => x.IndexOf("name", StringComparison.CurrentCultureIgnoreCase) > -1).ThenByDescending(x => x.IndexOf("title", StringComparison.CurrentCultureIgnoreCase) > -1).FirstOrDefault("");
                                string safeNodeName = item.ContainsKey(nameColumn) ? item[nameColumn]?.ToString() ?? guid.ToString() : guid.ToString();
                                safeNodeName = new string(safeNodeName.Take(99).ToArray());
                                CreateContentItemParameters createParams = new(
                                                                    xbkDataClass.ClassName,
                                                                    safeNodeName,
                                                                    languageCode);
                                ContentItemData itemData = new(item);

                                try
                                {
                                    int id = await contentItemManager.Create(createParams, itemData);
                                    await contentItemManager.TryPublish(id, languageCode);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Error while copying data for Custom Table: {ClassName}", xbkDataClass.ClassName);
                                }

                            }

                            logger.LogInformation("Data imported for Custom Table: {ClassName}", xbkDataClass.ClassName);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error while copying data for Custom Table: {ClassName}", xbkDataClass.ClassName);
                        }
                    }

                    #endregion
                }
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
                dataClassInfo.ClassShortName = dataClassInfo.ClassName.Replace(".", "").Replace("_", "");

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
        var existingForm = new FormInfo(info.ClassFormDefinition);
        form.CombineWithForm(existingForm, new());
        info.ClassFormDefinition = form.GetXmlDefinition();
    }

    private async IAsyncEnumerable<Dictionary<string, object>> Data(ICmsClass srcClass, ICmsSite cmsSite)
    {
        using SqlConnection conn = new(configuration.KxConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {srcClass.ClassTableName}";
        using var reader = cmd.ExecuteReader();

        var formInfo = new FormInfo(srcClass.ClassFormDefinition);

        while (reader.Read())
        {
            var dict = new Dictionary<string, object>();
            for (int lp = 0; lp < reader.FieldCount; lp++)
            {
                string name = reader.GetName(lp);

                string control = formInfo.GetFields(true, true).Where(x => x.Name == name).FirstOrDefault()?.GetComponentName() ?? "";

                if (!columnNamesToRemove.Contains(name) && !reader.IsDBNull(lp))
                {
                    object value = reader.GetValue(lp);

                    if (control == Kx13FormControls.UserControlForText.MediaSelectionControl)
                    {
                        if (!string.IsNullOrWhiteSpace(value.ToString()))
                        {
                            List<object> mfis = [];

                            if (value is string link &&
            mediaLinkServiceFactory.Create().MatchMediaLink(link, cmsSite.SiteID) is (true, var mediaLinkKind, var mediaKind, var path, var mediaGuid, _, _) result)
                            {
                                if (mediaLinkKind == MediaLinkKind.Path)
                                {
                                    // path needs to be converted to GUID
                                    if (mediaKind == MediaKind.Attachment && path != null)
                                    {

                                        switch (await attachmentMigrator.TryMigrateAttachmentByPath(path, $"__{name}"))
                                        {
                                            case MigrateAttachmentResultMediaFile(true, _, var x, _):
                                            {
                                                mfis = [new AssetRelatedItem { Identifier = x.FileGUID, Dimensions = new AssetDimensions { Height = x.FileImageHeight, Width = x.FileImageWidth }, Name = x.FileName, Size = x.FileSize }];
                                                logger.LogTrace("'{FieldName}' migrated Match={Value}", name, result);
                                                break;
                                            }
                                            case MigrateAttachmentResultContentItem { Success: true, ContentItemGuid: { } contentItemGuid }:
                                            {
                                                mfis =
                                                [
                                                    new ContentItemReference { Identifier = contentItemGuid }
                                                ];
                                                logger.LogTrace("'{FieldName}' migrated Match={Value}", name, result);
                                                break;
                                            }
                                            default:
                                            {
                                                logger.LogTrace("Unsuccessful attachment migration '{Field}': '{Value}' - '{Match}'", name, path, result);
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (mediaLinkKind == MediaLinkKind.DirectMediaPath)
                                {
                                    if (mediaKind == MediaKind.MediaFile)
                                    {
                                        var sourceMediaFile = MediaHelper.GetMediaFile(result, modelFacade);
                                        if (sourceMediaFile != null)
                                        {
                                            if (configuration.MigrateMediaToMediaLibrary)
                                            {
                                                if (entityIdentityFacade.Translate(sourceMediaFile) is { } mf && mediaFileFacade.GetMediaFile(mf.Identity) is { } x)
                                                {
                                                    mfis = [new AssetRelatedItem { Identifier = x.FileGUID, Dimensions = new AssetDimensions { Height = x.FileImageHeight, Width = x.FileImageWidth }, Name = x.FileName, Size = x.FileSize }];
                                                }
                                            }
                                            else
                                            {
                                                var (ownerContentItemGuid, _) = assetFacade.GetRef(sourceMediaFile);
                                                mfis =
                                                [
                                                    new ContentItemReference { Identifier = ownerContentItemGuid }
                                                ];
                                                logger.LogTrace("MediaFile migrated from media file '{Field}': '{Value}'", name, result);
                                            }
                                        }
                                    }
                                }

                                if (mediaGuid is { } mg)
                                {
                                    if (mediaKind == MediaKind.Attachment)
                                    {
                                        switch (await attachmentMigrator.MigrateAttachment(mg, $"__{name}", cmsSite.SiteID))
                                        {
                                            case MigrateAttachmentResultMediaFile(true, _, var x, _):
                                            {
                                                mfis = [new AssetRelatedItem { Identifier = x.FileGUID, Dimensions = new AssetDimensions { Height = x.FileImageHeight, Width = x.FileImageWidth }, Name = x.FileName, Size = x.FileSize }];
                                                logger.LogTrace("MediaFile migrated from attachment '{Field}': '{Value}'", name, mg);
                                                break;
                                            }
                                            case MigrateAttachmentResultContentItem { Success: true, ContentItemGuid: { } contentItemGuid }:
                                            {
                                                mfis =
                                                [
                                                    new ContentItemReference { Identifier = contentItemGuid }
                                                ];
                                                logger.LogTrace("Content item migrated from attachment '{Field}': '{Value}' to {ContentItemGUID}", name, mg, contentItemGuid);
                                                break;
                                            }
                                            default:
                                            {
                                                break;
                                            }
                                        }
                                    }

                                    if (mediaKind == MediaKind.MediaFile)
                                    {
                                        var sourceMediaFile = modelFacade.SelectWhere<IMediaFile>("FileGUID = @mediaFileGuid AND FileSiteID = @fileSiteID", new SqlParameter("mediaFileGuid", mg), new SqlParameter("fileSiteID", cmsSite.SiteID))
                                            .FirstOrDefault();
                                        if (sourceMediaFile != null)
                                        {
                                            if (configuration.MigrateMediaToMediaLibrary)
                                            {
                                                if (entityIdentityFacade.Translate(sourceMediaFile) is { } mf && mediaFileFacade.GetMediaFile(mf.Identity) is { } x)
                                                {
                                                    mfis = [new AssetRelatedItem { Identifier = x.FileGUID, Dimensions = new AssetDimensions { Height = x.FileImageHeight, Width = x.FileImageWidth }, Name = x.FileName, Size = x.FileSize }];
                                                }
                                            }
                                            else
                                            {
                                                var (ownerContentItemGuid, _) = assetFacade.GetRef(sourceMediaFile);
                                                mfis =
                                                [
                                                    new ContentItemReference { Identifier = ownerContentItemGuid }
                                                ];
                                                logger.LogTrace("MediaFile migrated from media file '{Field}': '{Value}'", name, mg);
                                            }
                                        }
                                    }
                                }
                            }

                            dict.Add(name, mfis);
                        }
                    }
                    else
                    {
                        dict.Add(name, value);
                    }
                }
            }
            yield return dict;
        }
    }
}
