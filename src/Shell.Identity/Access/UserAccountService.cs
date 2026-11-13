using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shell.Identity.Audit;
using Shell.Identity.Data;

namespace Shell.Identity.Access;

/// <summary>Platform-level account operations, run as host commands (<c>dotnet Host.dll disable-user ...</c>).</summary>
public sealed class UserAccountService(
    AuthDbContext db,
    UserManager<User> users,
    GrantRevoker grants,
    TenantAccessService access,
    AuditLog audit)
{
    /// <summary>
    /// Disabling blocks sign-in, revokes every grant of every tenant, and changes the security stamp so existing sessions on the
    /// authorization server end within the stamp validation interval. API access tokens stop at the next membership check.
    /// </summary>
    public async Task<bool> SetDisabledAsync(string email, bool disabled, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        return user is not null && await SetDisabledCoreAsync(user, disabled, actorId: null, ct);
    }

    public async Task<bool> SetDisabledByIdAsync(Guid userId, bool disabled, Guid? actorId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is not null && await SetDisabledCoreAsync(user, disabled, actorId, ct);
    }

    public async Task<bool> SetPlatformAdminAsync(string email, bool isPlatformAdmin, Guid? actorId, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return false;
        }

        user.IsPlatformAdmin = isPlatformAdmin;
        audit.Add(isPlatformAdmin ? AuditEvents.PlatformAdminGranted : AuditEvents.PlatformAdminRevoked, user.Id, actorId: actorId);
        await users.UpdateSecurityStampAsync(user); // Saves the user and the audit event.
        return true;
    }

    private async Task<bool> SetDisabledCoreAsync(User user, bool disabled, Guid? actorId, CancellationToken ct)
    {
        user.IsDisabled = disabled;
        audit.Add(disabled ? AuditEvents.UserDisabled : AuditEvents.UserEnabled, user.Id, actorId: actorId);
        await users.UpdateSecurityStampAsync(user); // Saves the user and the audit event.

        if (disabled)
        {
            await grants.RevokeAsync(user.Id, tenantId: null, ct);
        }

        foreach (var tenantId in await db.Memberships.Where(m => m.UserId == user.Id).Select(m => m.TenantId).ToListAsync(ct))
        {
            await access.InvalidateAsync(tenantId, user.Id, ct);
        }

        return true;
    }
}
