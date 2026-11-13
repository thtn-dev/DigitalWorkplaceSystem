using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Audit;
using Shell.Identity.Data;

namespace Shell.Identity.Pages.Account;

public sealed class ConfirmEmailModel(UserManager<User> users, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool Confirmed { get; private set; }

    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (UserId == Guid.Empty || string.IsNullOrWhiteSpace(Token))
        {
            Error = "This confirmation link is invalid or expired.";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (UserId == Guid.Empty || string.IsNullOrWhiteSpace(Token))
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var user = await users.FindByIdAsync(UserId.ToString());
        if (user is null)
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var result = await users.ConfirmEmailAsync(user, Token);
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
