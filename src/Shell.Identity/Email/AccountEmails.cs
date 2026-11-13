using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shell.Identity.Email;

/// <summary>
/// Sends account emails. The default implementation only logs the links (development, demo);
/// production registers <see cref="SmtpAccountEmailSender"/> when <c>Identity:Email:Host</c> is set.
/// </summary>
public interface IAccountEmailSender
{
    Task SendInvitationAsync(string email, string tenantName, string role, string link);

    /// <summary>Password reset, or first password of an imported account.</summary>
    Task SendSetPasswordAsync(string email, string link);
}

internal sealed class LoggingAccountEmailSender(ILogger<LoggingAccountEmailSender> logger) : IAccountEmailSender
{
    public Task SendInvitationAsync(string email, string tenantName, string role, string link)
    {
        logger.LogWarning("No email provider configured. Invitation of {Email} to {Tenant} as {Role}: {Link}", email, tenantName, role, link);
        return Task.CompletedTask;
    }

    public Task SendSetPasswordAsync(string email, string link)
    {
        logger.LogWarning("No email provider configured. Set-password link for {Email}: {Link}", email, link);
        return Task.CompletedTask;
    }
}

internal sealed class SmtpAccountEmailSender(IOptions<ShellIdentityOptions> options, ILogger<SmtpAccountEmailSender> logger) : IAccountEmailSender
{
    public Task SendInvitationAsync(string email, string tenantName, string role, string link)
    {
        var subject = $"Invitation to join {SanitizeHeader(tenantName)}";
        var text = $"You have been invited to join {tenantName} as {role}.\n\nAccept your invitation: {link}";
        var html =
            $"<p>You have been invited to join <strong>{WebUtility.HtmlEncode(tenantName)}</strong> as <strong>{WebUtility.HtmlEncode(role)}</strong>.</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Accept invitation</a></p>";
        return SendAsync(email, subject, text, html);
    }

    public Task SendSetPasswordAsync(string email, string link)
    {
        const string subject = "Set your password";
        var text = $"Use the link below to set or reset your password:\n\n{link}";
        var html =
            "<p>Use the link below to set or reset your password:</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Set password</a></p>";
        return SendAsync(email, subject, text, html);
    }

    private async Task SendAsync(string to, string subject, string plainTextBody, string htmlBody)
    {
        var email = options.Value.Email;
        using var message = new MailMessage(new MailAddress(email.FromAddress, email.FromName), new MailAddress(to))
        {
            Subject = SanitizeHeader(subject),
            SubjectEncoding = Encoding.UTF8,
            Body = plainTextBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        using var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, Encoding.UTF8, MediaTypeNames.Text.Html);
        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient(email.Host!, email.Port)
        {
            EnableSsl = email.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(email.Username))
        {
            client.Credentials = new NetworkCredential(email.Username, email.Password);
        }

        await client.SendMailAsync(message);
        logger.LogInformation("Sent account email {Subject} to {Recipient}", message.Subject, to);
    }

    private static string SanitizeHeader(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>Absolute links to pages of the authorization server, put in emails.</summary>
public sealed class AccountLinks(IOptions<ShellIdentityOptions> options)
{
    public string Invitation(string token) => Build($"invitations/accept?token={Uri.EscapeDataString(token)}");

    public string SetPassword(Guid userId, string token) =>
        Build($"account/set-password?userId={userId}&token={Uri.EscapeDataString(token)}");

    private string Build(string relative) => new Uri(options.Value.Issuer!, relative).ToString();
}
