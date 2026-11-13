using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Audit;
using Shell.Identity.Data;
using Shell.Identity.Email;

namespace Shell.Identity.Pages.Account;

/// <summary>Creates a personal account and sends an email confirmation link. Grants no tenant membership.</summary>
public sealed class RegisterModel(
    SignInManager<User> signIn,
    UserManager<User> users,
    IAccountEmailSender emails,
    AccountLinks links,
    AuditLog audit,
    TimeProvider time) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public List<string> Errors { get; } = [];

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var email = Email.Trim();
        var user = new User { Id = Guid.CreateVersion7(), UserName = email, Email = email, CreatedAt = time.GetUtcNow() };
        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            Errors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        await audit.RecordAsync(AuditEvents.UserRegistered, user.Id, data: new { method = "pwd" });
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await emails.SendEmailConfirmationAsync(email, links.ConfirmEmail(user.Id, token));

        await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }
}
