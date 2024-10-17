using CMS.ContentEngine;
using CMS.Websites;

using Kentico.Xperience.UMT.Model;
using Kentico.Xperience.UMT.Services;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Migration.Tool.Common;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.Common.MigrationProtocol;
using Migration.Tool.Core.KX12.Helpers;
using Migration.Tool.KX12;
using Migration.Tool.KX12.Context;

namespace Migration.Tool.Core.KX12.Handlers;

// ReSharper disable once UnusedType.Global
public class MigrateSitesCommandHandler(
    ILogger<MigrateSitesCommandHandler> logger,
    IDbContextFactory<KX12Context> kx12ContextFactory,
    IProtocol protocol,
    IImporter importer)
    : IRequestHandler<MigrateSitesCommand, CommandResult>
{
    public async Task<CommandResult> Handle(MigrateSitesCommand request, CancellationToken cancellationToken)
    {
        await using var kx12Context = await kx12ContextFactory.CreateDbContextAsync(cancellationToken);
        var migratedCultureCodes = new Dictionary<string, ContentLanguageInfo>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var kx12CmsSite in kx12Context.CmsSites.Include(s => s.Cultures))
        {
            protocol.FetchedSource(kx12CmsSite);
            logger.LogTrace("Migrating site {SiteName} with SiteGuid {SiteGuid}", kx12CmsSite.SiteName, kx12CmsSite.SiteGuid);

            string defaultCultureCode = GetSiteCulture(kx12CmsSite);
            var migratedSiteCultures = kx12CmsSite.Cultures.ToList();
            if (!migratedSiteCultures.Any(x => x.CultureCode.Equals(defaultCultureCode, StringComparison.InvariantCultureIgnoreCase)))
            {
                await using var ctx = await kx12ContextFactory.CreateDbContextAsync(cancellationToken);
                if (ctx.CmsCultures.FirstOrDefault(c => c.CultureCode == defaultCultureCode) is { } defaultCulture)
                {
                    migratedSiteCultures.Add(defaultCulture);
                }
            }

            foreach (var cmsCulture in migratedSiteCultures)
            {
                var existing = ContentLanguageInfoProvider.ProviderObject.Get()
                    .WhereEquals(nameof(ContentLanguageInfo.ContentLanguageCultureFormat), cmsCulture.CultureCode)
                    .FirstOrDefault();

                if (existing != null && existing.ContentLanguageGUID != cmsCulture.CultureGuid)
                {
                    existing.ContentLanguageGUID = cmsCulture.CultureGuid;
                    existing.Update();
                }

                if (migratedCultureCodes.ContainsKey(cmsCulture.CultureCode))
                {
                    continue;
                }

                var langResult = await importer.ImportAsync(new ContentLanguageModel
                {
                    ContentLanguageGUID = cmsCulture.CultureGuid,
                    ContentLanguageDisplayName = cmsCulture.CultureName,
                    ContentLanguageName = cmsCulture.CultureCode,
                    ContentLanguageIsDefault = true,
                    ContentLanguageFallbackContentLanguageGuid = null,
                    ContentLanguageCultureFormat = cmsCulture.CultureCode
                });

                if (langResult is { Success: true, Imported: ContentLanguageInfo importedLanguage })
                {
                    migratedCultureCodes.TryAdd(cmsCulture.CultureCode, importedLanguage);
                    logger.LogTrace("Imported language {Language} from {Culture}", importedLanguage.ContentLanguageName, cmsCulture.CultureCode);
                }
            }

            // var homePageNodeAliasPath = KenticoHelper.GetSettingsKey(_kx12ContextFactory, kx12CmsSite.SiteId, SettingsKeys.CMSDefaultAliasPath);
            int? cookieLevel = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, SettingsKeys.CMSDefaultCookieLevel) switch
            {
                "all" => CookieLevelConstants.ALL,
                "visitor" => CookieLevelConstants.VISITOR,
                "editor" => CookieLevelConstants.EDITOR,
                "system" => CookieLevelConstants.SYSTEM,
                "essential" => CookieLevelConstants.ESSENTIAL,
                _ => null
            };
            bool? storeFormerUrls = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, "CMSStoreFormerUrls") is string storeFormerUrlsStr
                ? bool.TryParse(storeFormerUrlsStr, out bool sfu) ? sfu : null
                : true;

            var channelResult = await importer.ImportAsync(new ChannelModel { ChannelDisplayName = kx12CmsSite.SiteDisplayName, ChannelName = kx12CmsSite.SiteName, ChannelGUID = kx12CmsSite.SiteGuid, ChannelType = ChannelType.Website });

            var webSiteChannelResult = await importer.ImportAsync(new WebsiteChannelModel
            {
                WebsiteChannelGUID = kx12CmsSite.SiteGuid,
                WebsiteChannelChannelGuid = kx12CmsSite.SiteGuid,
                WebsiteChannelDomain = kx12CmsSite.SiteDomainName,
                // WebsiteChannelHomePage = homePageNodeAliasPath,
                WebsiteChannelPrimaryContentLanguageGuid = migratedCultureCodes[defaultCultureCode].ContentLanguageGUID,
                WebsiteChannelDefaultCookieLevel = cookieLevel,
                WebsiteChannelStoreFormerUrls = storeFormerUrls
            });

            if (!webSiteChannelResult.Success)
            {
                if (webSiteChannelResult.ModelValidationResults != null)
                {
                    foreach (var mvr in webSiteChannelResult.ModelValidationResults)
                    {
                        logger.LogError("Invalid channel properties {Members}: {ErrorMessage}", string.Join(", ", mvr.MemberNames), mvr.ErrorMessage);
                    }
                }
                else
                {
                    logger.LogError(webSiteChannelResult.Exception, "Failed to migrate site");
                }

                return new CommandFailureResult();
            }

            if (webSiteChannelResult.Imported is WebsiteChannelInfo webSiteChannel)
            {
                string? cmsReCaptchaPublicKey = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, "CMSReCaptchaPublicKey");
                string? cmsReCaptchaPrivateKey = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, "CMSReCaptchaPrivateKey");

                WebsiteCaptchaSettingsInfo? reCaptchaSettings = null;
                string? cmsReCaptchaV3PrivateKey = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, "CMSReCaptchaV3PrivateKey");
                string? cmsRecaptchaV3PublicKey = KenticoHelper.GetSettingsKey(kx12ContextFactory, kx12CmsSite.SiteId, "CMSRecaptchaV3PublicKey");
                double? cmsRecaptchaV3Threshold = KenticoHelper.GetSettingsKey<double>(kx12ContextFactory, kx12CmsSite.SiteId, "CMSRecaptchaV3Threshold");

                if (!string.IsNullOrWhiteSpace(cmsReCaptchaV3PrivateKey) || !string.IsNullOrWhiteSpace(cmsRecaptchaV3PublicKey))
                {
                    reCaptchaSettings = new WebsiteCaptchaSettingsInfo
                    {
                        WebsiteCaptchaSettingsWebsiteChannelID = webSiteChannel.WebsiteChannelID,
                        WebsiteCaptchaSettingsReCaptchaSiteKey = cmsRecaptchaV3PublicKey,
                        WebsiteCaptchaSettingsReCaptchaSecretKey = cmsReCaptchaV3PrivateKey,
                        WebsiteCaptchaSettingsReCaptchaThreshold = cmsRecaptchaV3Threshold ?? 0.5d,
                        WebsiteCaptchaSettingsReCaptchaVersion = ReCaptchaVersion.ReCaptchaV3
                    };
                }

                if (!string.IsNullOrWhiteSpace(cmsReCaptchaPublicKey) || !string.IsNullOrWhiteSpace(cmsReCaptchaPrivateKey))
                {
                    if (reCaptchaSettings is not null)
                    {
                        logger.LogError("""
                                        Conflicting settings found, ReCaptchaV2 and ReCaptchaV3 is set simultaneously.
                                        Remove setting keys 'CMSReCaptchaPublicKey', 'CMSReCaptchaPrivateKey'
                                        or remove setting keys 'CMSReCaptchaV3PrivateKey', 'CMSRecaptchaV3PublicKey', 'CMSRecaptchaV3Threshold'.
                                        """);
                        throw new InvalidOperationException("Invalid ReCaptcha settings");
                    }

                    reCaptchaSettings = new WebsiteCaptchaSettingsInfo
                    {
                        WebsiteCaptchaSettingsWebsiteChannelID = webSiteChannel.WebsiteChannelID,
                        WebsiteCaptchaSettingsReCaptchaSiteKey = cmsReCaptchaPublicKey,
                        WebsiteCaptchaSettingsReCaptchaSecretKey = cmsReCaptchaPrivateKey,
                        WebsiteCaptchaSettingsReCaptchaVersion = ReCaptchaVersion.ReCaptchaV2
                    };
                }

                if (reCaptchaSettings != null)
                {
                    WebsiteCaptchaSettingsInfo.Provider.Set(reCaptchaSettings);
                }
            }
        }

        return new GenericCommandResult();
    }

    private string GetSiteCulture(KX12M.CmsSite site)
    {
        // simplified logic from CMS.DocumentEngine.DefaultPreferredCultureEvaluator.Evaluate()
        // domain alias skipped, HttpContext logic skipped
        string? siteCulture = site.SiteDefaultVisitorCulture.NullIf(string.Empty)
                              ?? KenticoHelper.GetSettingsKey(kx12ContextFactory, site.SiteId, SettingsKeys.CMSDefaultCultureCode);

        return siteCulture
               ?? throw new InvalidOperationException("Unknown site culture");
    }
}