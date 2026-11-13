using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Audit;
using Shell.Identity.Data;

namespace Shell.Identity.Pages.Account;

public sealed class ConfirmEmailModel(UserManager<User> users, AuditLog audit) : PageModel
{
    public bool Confirmed { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid userId, string? token)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var result = await users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        await audit.RecordAsync(AuditEvents.EmailConfirmed, user.Id);
        Confirmed = true;
        return Page();
    }
}
