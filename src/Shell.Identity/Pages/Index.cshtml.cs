using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Data;
using Shell.Identity.Email;

namespace Shell.Identity.Pages;

public sealed class IndexModel(UserManager<User> users, IAccountEmailSender emails, AccountLinks links) : PageModel
{
    public string? Email { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task OnGetAsync(string? status = null)
    {
        StatusMessage = status;
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } account)
        {
            Email = account.Email;
            EmailConfirmed = account.EmailConfirmed;
            TwoFactorEnabled = account.TwoFactorEnabled;
        }
    }

    public async Task<IActionResult> OnPostResendConfirmationAsync()
    {
        if (User.Identity?.IsAuthenticated != true || await users.GetUserAsync(User) is not { } account)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!account.EmailConfirmed && !string.IsNullOrWhiteSpace(account.Email))
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(account);
            await emails.SendEmailConfirmationAsync(account.Email, links.ConfirmEmail(account.Id, token));
        }

        return RedirectToPage(new { status = "Confirmation email sent." });
    }
}
