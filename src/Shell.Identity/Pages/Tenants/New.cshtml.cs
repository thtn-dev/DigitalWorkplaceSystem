using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Shell.Identity.Data;
using Shell.Identity.Tenants;

namespace Shell.Identity.Pages.Tenants;

[Authorize]
public sealed class NewTenantModel(
    UserManager<User> users,
    TenantProvisioningService provisioning,
    IOptions<ShellIdentityOptions> options) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Subdomain { get; set; } = "";

    public string BaseDomain => options.Value.SelfSignUpBaseDomain;

    public bool EmailConfirmed { get; private set; }

    public string? Error { get; private set; }

    public string? CreatedDomain { get; private set; }

    public Guid? CreatedTenantId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = "/tenants/new" });
        }

        EmailConfirmed = user.EmailConfirmed;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = "/tenants/new" });
        }

        EmailConfirmed = user.EmailConfirmed;
        var result = await provisioning.CreateTrialAsync(user, Name, Subdomain, HttpContext.RequestAborted);
        if (result.Outcome != TenantProvisioningOutcome.Ok)
        {
            Error = result.Detail;
            return Page();
        }

        CreatedDomain = result.Domain;
        CreatedTenantId = result.TenantId;
        return Page();
    }
}
