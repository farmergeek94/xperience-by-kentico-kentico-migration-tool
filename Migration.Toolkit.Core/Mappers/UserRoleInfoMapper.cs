namespace Migration.Toolkit.Core.Mappers;

using CMS.Membership;
using Microsoft.Extensions.Logging;
using Migration.Toolkit.Core.Abstractions;
using Migration.Toolkit.Core.Contexts;
using Migration.Toolkit.Core.MigrationProtocol;


public class UserRoleInfoMapper: EntityMapperBase<KX13M.CmsUserRole, UserRoleInfo>
{
    public UserRoleInfoMapper(ILogger<UserRoleInfoMapper> logger, PrimaryKeyMappingContext pkContext, IProtocol protocol) : base(logger, pkContext, protocol)
    {
    }

    protected override UserRoleInfo? CreateNewInstance(KX13M.CmsUserRole source, MappingHelper mappingHelper, AddFailure addFailure)
        => UserRoleInfo.New();

    protected override UserRoleInfo MapInternal(KX13M.CmsUserRole source, UserRoleInfo target, bool newInstance, MappingHelper mappingHelper, AddFailure addFailure)
    {
        if (mappingHelper.TranslateRequiredId<KX13M.CmsRole>(r => r.RoleId, source.RoleId, out var xbkRoleId))
        {
            target.RoleID = xbkRoleId;
        }

        if (mappingHelper.TranslateRequiredId<KX13M.CmsUser>(r => r.UserId, source.UserId, out var xbkUserId))
        {
            target.UserID = xbkUserId;
        }

        target.ValidTo = source.ValidTo.GetValueOrDefault();

        return target;
    }
}