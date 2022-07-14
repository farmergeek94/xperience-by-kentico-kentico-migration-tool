using System.Collections.Concurrent;
using CMS.FormEngine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Toolkit.Core.MigrationProtocol;
using Migration.Toolkit.KX13.Context;
using Migration.Toolkit.KX13.Models;

namespace Migration.Toolkit.Core.Convertors;

public class FormInfoDefinitionConvertor
{
    private readonly IMigrationProtocol _protocol;
    private readonly ILogger<FormInfoDefinitionConvertor> _logger;
    private readonly IDbContextFactory<KX13Context> _kx13ContextFactory;

    public FormInfoDefinitionConvertor(IMigrationProtocol protocol, ILogger<FormInfoDefinitionConvertor> logger, IDbContextFactory<KX13.Context.KX13Context> kx13ContextFactory)
    {
        _protocol = protocol;
        _logger = logger;
        _kx13ContextFactory = kx13ContextFactory;
    }

    private ConcurrentDictionary<string, KX13M.CmsFormUserControl?> _userControlsCache = new(StringComparer.InvariantCultureIgnoreCase);
    private CmsFormUserControl? GetFormControlDefinition(string userControlCodeName)
    {
        var kx13Context = _kx13ContextFactory.CreateDbContext();
        return _userControlsCache.GetOrAdd(userControlCodeName, s =>
        {
            return kx13Context.CmsFormUserControls.FirstOrDefault(x => x.UserControlCodeName == userControlCodeName);
        });
    }
    
    public string ConvertToKxo(string formDefinitionXml)
    {
        // BizFormInfoProvider.GetBizFormFileColumns()
        
        var formInfo = new FormInfo(formDefinitionXml);
        foreach (var columnName in formInfo.GetColumnNames())
        {
            var field = formInfo.GetFormField(columnName);
            var controlName = field.Settings["controlname"]?.ToString()?.ToLowerInvariant();

            var formControlDefinition = GetFormControlDefinition(controlName);
            
            
            
            
            // TODO tk: 2022-06-28 refresh check... check is dead with recent K-API change 
            // switch (controlName)
            // {
            //     /// <summary>Calendar.</summary>
            //     case FormFieldControlName.CALENDAR:
            //     {
            //         break;
            //     }
            //     /// <summary>Check box.</summary>
            //     case FormFieldControlName.CHECKBOX:
            //     {
            //         break;
            //     }
            //     /// <summary>Drop down list.</summary>
            //     case FormFieldControlName.DROPDOWNLIST:
            //     {
            //         break;
            //     }
            //     /// <summary>File selector.</summary>
            //     case FormFieldControlName.FILESELECTION:
            //     {
            //         break;
            //     }
            //     /// <summary>HTML area.</summary>
            //     case FormFieldControlName.HTMLAREA:
            //     {
            //         break;
            //     }
            //     /// <summary>Image selector.</summary>
            //     case FormFieldControlName.IMAGESELECTION:
            //     {
            //         break;
            //     }
            //     /// <summary>Media selector.</summary>
            //     case FormFieldControlName.MEDIASELECTION:
            //     {
            //         break;
            //     }
            //     /// <summary>Label.</summary>
            //     case FormFieldControlName.LABEL:
            //     {
            //         break;
            //     }
            //     /// <summary>Multiple choice control.</summary>
            //     case FormFieldControlName.MULTIPLECHOICE:
            //     {
            //         break;
            //     }
            //     /// <summary>Radio buttons control.</summary>
            //     case FormFieldControlName.RADIOBUTTONS:
            //     {
            //         break;
            //     }
            //     /// <summary>Text area.</summary>
            //     case FormFieldControlName.TEXTAREA:
            //     {
            //         break;
            //     }
            //     /// <summary>Text box.</summary>
            //     case FormFieldControlName.TEXTBOX:
            //     {
            //         break;
            //     }
            //     /// <summary>Upload control.</summary>
            //     case FormFieldControlName.UPLOAD:
            //     {
            //         break;
            //     }
            //     /// <summary>List box.</summary>
            //     case FormFieldControlName.LISTBOX:
            //     {
            //         break;
            //     }
            //     /// <summary>Document attachments control.</summary>
            //     case FormFieldControlName.DOCUMENT_ATTACHMENTS:
            //     {
            //         break;
            //     }
            //     case "unknown":
            //     {
            //         // TODO tk: 2022-06-08 log explicitly unknown control
            //         break;
            //     }
            //     default:
            //     {
            //         if(controlName != null)
            //         {
            //             // TODO tk: 2022-06-08 somehow check if custom control exists in target instance?
            //             _protocol.NeedsManualAction(HandbookReferences.CreatePossiblyCustomControlNeedToBeMigrated(controlName), formDefinitionXml);
            //         }
            //
            //         break;
            //     }
            // }
        }

        // foreach (var dataDefinitionItem in formInfo.ItemsList)
        // {
        //     switch (dataDefinitionItem)
        //     {
        //         case IField field:
        //         {
        //             
        //             break;
        //         }
        //         // case CMS.DataEngine.FieldInfo fieldInfo:
        //         // {
        //         //     fieldInfo.
        //         //     break;
        //         // }
        //         default:
        //         {
        //             // ConvertToKxo()
        //             break;
        //         }
        //     }
        // }

        return formInfo.GetXmlDefinition();
    }
}