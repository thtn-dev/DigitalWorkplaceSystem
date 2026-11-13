using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shell.Identity.Email;

namespace Shell.Testing;

/// <summary>Keeps the links the application would have emailed, per recipient.</summary>
public sealed class TestEmailSender : IAccountEmailSender
{
    private readonly ConcurrentDictionary<string, string> _lastLink = new(StringComparer.OrdinalIgnoreCase);

    public Task SendInvitationAsync(string email, string tenantName, string role, string link) => Keep(email, link);

    public Task SendSetPasswordAsync(string email, string link) => Keep(email, link);

    /// <summary>Path and query of the last link sent to <paramref name="email"/> (pages live on the authorization server host).</summary>
    public string LastLinkTo(string email) =>
        _lastLink.TryGetValue(email, out var link) ? new Uri(link).PathAndQuery : throw new InvalidOperationException($"No email sent to {email}.");

    private Task Keep(string email, string link)
    {
        _lastLink[email] = link;
        return Task.CompletedTask;
    }
}

public sealed record ReceivedEmail(
    IReadOnlyList<string> Recipients,
    string Subject,
    string RawData,
    string PlainTextBody,
    string HtmlBody,
    string DecodedBody);

/// <summary>
/// Minimal in-process SMTP server on loopback for verifying <see cref="IAccountEmailSender"/> SMTP delivery end to end.
/// </summary>
public sealed partial class LoopbackSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<ReceivedEmail> _messages = new();

    public LoopbackSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public ReceivedEmail SingleMessageTo(string recipient)
    {
        var matches = _messages.Where(m => m.Recipients.Contains(recipient, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(matches.Count == 1, $"Expected 1 email to {recipient}, found {matches.Count}.");
        return matches[0];
    }

    public static string FirstLinkIn(ReceivedEmail email)
    {
        var source = string.IsNullOrEmpty(email.PlainTextBody) ? email.DecodedBody : email.PlainTextBody;
        var match = UrlPattern().Match(source);
        Assert.True(match.Success, $"No link found in email body:\n{source}");
        return match.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var owned = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 localhost ESMTP");
        var recipients = new List<string>();
        var data = new StringBuilder();
        var inData = false;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (inData)
            {
                if (line == ".")
                {
                    inData = false;
                    _messages.Enqueue(ParseMessage(recipients, data.ToString()));
                    data.Clear();
                    recipients = [];
                    await writer.WriteLineAsync("250 OK");
                    continue;
                }

                data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
                continue;
            }

            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-localhost");
                await writer.WriteLineAsync("250-8BITMIME");
                await writer.WriteLineAsync("250-SMTPUTF8");
                await writer.WriteLineAsync("250 OK");
            }
            else if (line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("NOOP", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
                {
                    recipients.Clear();
                }

                await writer.WriteLineAsync("250 OK");
            }
            else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
            {
                var start = line.IndexOf('<');
                var end = line.IndexOf('>');
                if (start >= 0 && end > start)
                {
                    recipients.Add(line[(start + 1)..end]);
                }

                await writer.WriteLineAsync("250 OK");
            }
            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                inData = true;
                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
            }
            else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 Bye");
                break;
            }
            else
            {
                await writer.WriteLineAsync("500 Unrecognized command");
            }
        }
    }

    private static ReceivedEmail ParseMessage(List<string> recipients, string raw)
    {
        var splitIndex = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = splitIndex >= 0 ? raw[..splitIndex] : raw;
        var body = splitIndex >= 0 ? raw[(splitIndex + 4)..] : "";

        var subjectMatch = SubjectHeader().Match(headers);
        var subject = subjectMatch.Success ? DecodeHeaderWord(subjectMatch.Groups[1].Value.Trim()) : "";

        var plain = new StringBuilder();
        var html = new StringBuilder();
        var all = new StringBuilder();

        var boundaryMatch = BoundaryPattern().Match(headers);
        if (boundaryMatch.Success)
        {
            var boundary = "--" + boundaryMatch.Groups[1].Value;
            foreach (var part in body.Split(boundary, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.TrimStart('\r', '\n');
                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var partSplit = trimmed.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (partSplit < 0)
                {
                    continue;
                }

                var partHeaders = trimmed[..partSplit];
                var partBody = trimmed[(partSplit + 4)..].TrimEnd('\r', '\n');
                var decoded = DecodeBodyPart(partBody, partHeaders);
                all.AppendLine(decoded);

                if (partHeaders.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    html.Append(decoded);
                }
                else if (partHeaders.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    plain.Append(decoded);
                }
            }
        }
        else
        {
            var decoded = DecodeBodyPart(body, headers);
            all.Append(decoded);
            if (headers.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                html.Append(decoded);
            }
            else
            {
                plain.Append(decoded);
            }
        }

        return new ReceivedEmail([.. recipients], subject, raw, plain.ToString(), html.ToString(), all.ToString());
    }

    private static string DecodeBodyPart(string body, string headers)
    {
        if (headers.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            var cleaned = body.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
            return Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
        }

        if (headers.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeQuotedPrintable(body);
        }

        return body;
    }

    private static string DecodeQuotedPrintable(string input)
    {
        var unfolded = input.Replace("=\r\n", "", StringComparison.Ordinal).Replace("=\n", "", StringComparison.Ordinal);
        var bytes = new List<byte>(unfolded.Length);
        for (var i = 0; i < unfolded.Length; i++)
        {
            if (unfolded[i] == '=' && i + 2 < unfolded.Length
                && byte.TryParse(unfolded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes([unfolded[i]]));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string DecodeHeaderWord(string value)
    {
        if (value.StartsWith("=?utf-8?B?", StringComparison.OrdinalIgnoreCase) && value.EndsWith("?=", StringComparison.Ordinal))
        {
            var payload = value["=?utf-8?B?".Length..^2];
            return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }

        if (value.StartsWith("=?utf-8?Q?", StringComparison.OrdinalIgnoreCase) && value.EndsWith("?=", StringComparison.Ordinal))
        {
            var payload = value["=?utf-8?Q?".Length..^2].Replace('_', ' ');
            return DecodeQuotedPrintable(payload);
        }

        return value;
    }

    [GeneratedRegex(@"^Subject:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SubjectHeader();

    [GeneratedRegex(@"boundary=""?([^""\s;]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();
}

/// <summary>
/// Stand-in for a social provider (Google...): the challenge immediately "returns" from the provider with the email and subject
/// given in request headers, exactly like a real remote handler signing into the external cookie.
/// </summary>
public sealed class FakeSocialHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "fake-social";
    public const string EmailHeader = "X-Fake-Email";
    public const string SubjectHeader = "X-Fake-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Request.Headers[SubjectHeader].ToString()),
            new Claim(ClaimTypes.Email, Request.Headers[EmailHeader].ToString()),
            new Claim("email_verified", "true"),
        ], SchemeName);
        await Context.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(identity), properties);
        Response.Redirect(properties.RedirectUri!);
    }
}

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits), as computed by authenticator apps.</summary>
public static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

#pragma warning disable CA5350 // RFC 6238 TOTP mandates HMAC-SHA1
        var hash = HMACSHA1.HashData(Base32(base32Key), message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var bytes = new List<byte>();
        foreach (var c in text.Trim().TrimEnd('=').ToUpperInvariant().Where(c => c != ' '))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
