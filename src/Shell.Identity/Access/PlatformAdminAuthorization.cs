using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shell.Identity.Data;

namespace Shell.Identity.Access;

/// <summary>Authorization policy and claim names for platform-wide operations (<c>/admin/*</c>).</summary>
public static class PlatformPolicies
{
    public const string Admin = "PlatformAdmin";
    public const string ClaimType = "platform_admin";
}

internal sealed class PlatformAdminClaimsPrincipalFactory(
    UserManager<User> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<User>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.IsPlatformAdmin)
        {
            identity.AddClaim(new Claim(PlatformPolicies.ClaimType, "true"));
        }

        return identity;
    }
}

internal sealed record PlatformAdminRequirement : IAuthorizationRequirement;

/// <summary>Verifies against the database so revoking admin or disabling an account takes effect immediately.</summary>
internal sealed class PlatformAdminHandler(AuthDbContext db) : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        var rawId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(rawId, out var userId))
        {
            return;
        }

        var isActiveAdmin = await db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.IsPlatformAdmin && !u.IsDisabled);
        if (isActiveAdmin)
        {
            context.Succeed(requirement);
        }
    }
}
