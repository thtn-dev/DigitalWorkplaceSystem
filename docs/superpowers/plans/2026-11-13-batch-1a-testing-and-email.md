# Batch 1A — Shell.Testing & Real SMTP Email Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract a reusable `Shell.Testing` integration test kit (`B13`) with temporary tenant/member helpers, and implement production-ready SMTP email delivery (`I1`) with Mailpit in local Docker Compose.

**Architecture:** Move `PostgresFixture`, `ShellHostFactory`, `OidcTestClient`, and test doubles (`TestEmailSender`, `FakeSocialHandler`, `Totp`) into a class library `tests/Shell.Testing` using `xunit.v3.extensibility.core` + `xunit.v3.assert`, adding `CreateTenantAsync` and `AddMemberAsync` helpers that synchronize OpenIddict clients on the fly. Add `SmtpAccountEmailSender` and `ShellIdentityOptions.EmailOptions` (`Identity:Email`) in `Shell.Identity` using `System.Net.Mail.SmtpClient` with plain-text + HTML alternate views, registered automatically when `Identity:Email:Host` is configured and falling back to `LoggingAccountEmailSender` otherwise.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core, `System.Net.Mail.SmtpClient`, xUnit v3 (`4.0.2`), Testcontainers.PostgreSql, Mailpit (`axllent/mailpit:v1.24`).

**Spec:** `docs/core-gap-analysis.md` (§2 `B13`, §4 `I1`, §9 Đợt 1A)

## Global Constraints

- Target framework: `net10.0` (`global.json` SDK `10.0.100`, runner `Microsoft.Testing.Platform`).
- Solution file is `DigitalWorkspace.slnx`.
- All projects must build with `0 Warning(s)` and `0 Error(s)` under `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, and `TreatWarningsAsErrors=true`.
- `dotnet format DigitalWorkspace.slnx --verify-no-changes` must pass with LF line endings (`.editorconfig` / `.gitattributes`).
- Module assemblies must never depend on `Shell.Core`, `Shell.Identity`, or `Shell.Host`.

## Review Focus

1. **HTML injection and subject header CRLF in invitation emails (`I1`):** A `tenantName` containing `<script>` or `\r\n` must have `\r` and `\n` stripped from the email `Subject` (preventing `MailMessage.Subject` from throwing `ArgumentException`) and must be HTML-encoded in the `text/html` view.
2. **Query-string ampersands (`&`) in reset/invitation links (`I1`):** `SetPassword` links contain `?userId=...&token=...`; the `text/plain` view and decoded `text/html` link must preserve exact query parameters so following the emailed link resets the password without `&amp;token=` corruption.
3. **Invalid SMTP settings when `Identity:Email:Host` is configured (`I1`):** Setting `Identity:Email:Host` with a blank/invalid `FromAddress` (missing `@`) or out-of-range `Port` (`<= 0` or `> 65535`) must fail fast at host start-up with `OptionsValidationException`.
4. **Unconfigured `Identity:Email:Host` fallback (`I1`):** Leaving `Identity:Email:Host` empty or whitespace must pass options validation and register `LoggingAccountEmailSender` without attempting network connections.
5. **Temporary tenant helper in `Shell.Testing` missing OAuth client registration (`B13`):** Inserting rows into `catalog.tenants` and `catalog.tenant_domains` alone leaves OpenIddict unaware of `tenant-{id}`; `PostgresFixture.CreateTenantAsync` must also run `TenantClientSynchronizer.SynchronizeAsync` so tests can immediately obtain tokens for temporary tenants.

---

### Task 1: Extract Reusable `Shell.Testing` Library & Tenant Provisioning Helpers (`B13`)

**Files:**
- Modify: `Directory.Packages.props:22-27`
- Create: `tests/Shell.Testing/Shell.Testing.csproj`
- Create: `tests/Shell.Testing/PostgresFixture.cs`
- Create: `tests/Shell.Testing/OidcTestClient.cs`
- Create: `tests/Shell.Testing/TestDoubles.cs`
- Create: `tests/Shell.IntegrationTests/PostgresCollection.cs`
- Delete: `tests/Shell.IntegrationTests/PostgresFixture.cs`
- Delete: `tests/Shell.IntegrationTests/OidcTestClient.cs`
- Delete: `tests/Shell.IntegrationTests/TestDoubles.cs`
- Modify: `tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj`
- Modify: `tests/Shell.IntegrationTests/TenancyTests.cs`
- Modify: `tests/Shell.IntegrationTests/AuthenticationTests.cs`
- Modify: `tests/Shell.IntegrationTests/IamTests.cs`
- Modify: `tests/Shell.IntegrationTests/AccountSecurityTests.cs`
- Modify: `tests/Shell.IntegrationTests/TenantDbContextTests.cs`
- Modify: `DigitalWorkspace.slnx`

**Interfaces:**
- Consumes: `Shell.Core.Data.MigrationRunner`, `Shell.Identity.Clients.TenantClientSynchronizer`, `Shell.Identity.Email.IAccountEmailSender`
- Produces:
  - `Shell.Testing.PostgresFixture`
  - `Shell.Testing.PostgresFixture.CreateTenantAsync(ShellHostFactory host, string? domain = null, bool isActive = true, bool isIsolatedDb = false, string? connectionString = null) -> Task<(Guid Id, string Domain)>`
  - `Shell.Testing.PostgresFixture.AddMemberAsync(Guid tenantId, string email, string role = "member", string status = "active") -> Task`
  - `Shell.Testing.ShellHostFactory`
  - `Shell.Testing.OidcTestClient`
  - `Shell.Testing.TestEmailSender`, `Shell.Testing.FakeSocialHandler`, `Shell.Testing.Totp`

- [x] **Step 1: Add `xunit.v3.extensibility.core` and `xunit.v3.assert` to `Directory.Packages.props` and create `tests/Shell.Testing/Shell.Testing.csproj`**

In `Directory.Packages.props`, add to `<ItemGroup Label="Tests">`:
```xml
    <PackageVersion Include="xunit.v3.extensibility.core" Version="4.0.2" />
    <PackageVersion Include="xunit.v3.assert" Version="4.0.2" />
```

Create `tests/Shell.Testing/Shell.Testing.csproj` as a class library (do NOT set `<OutputType>Exe</OutputType>`):
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="xunit.v3.assert" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Host\Host.csproj" />
  </ItemGroup>

</Project>
```

Register `<Project Path="tests/Shell.Testing/Shell.Testing.csproj" />` under `<Folder Name="/tests/">` in `DigitalWorkspace.slnx`, and update `tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj` to reference `..\Shell.Testing\Shell.Testing.csproj`.

- [x] **Step 2: Move `PostgresFixture`, `ShellHostFactory`, `OidcTestClient`, and `TestDoubles` to `tests/Shell.Testing/` and add `CreateTenantAsync` & `AddMemberAsync`**

1. Move `PostgresFixture.cs`, `OidcTestClient.cs`, and `TestDoubles.cs` from `tests/Shell.IntegrationTests/` to `tests/Shell.Testing/` under namespace `Shell.Testing`.
2. Keep `PostgresCollection` in `tests/Shell.IntegrationTests/PostgresCollection.cs` (xUnit discovers `[CollectionDefinition]` in the test assembly):
```csharp
using Shell.Testing;

namespace Shell.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
```
3. In `tests/Shell.Testing/PostgresFixture.cs`, add `CreateTenantAsync` and `AddMemberAsync` to `PostgresFixture`:
```csharp
    /// <summary>Creates a tenant in the catalog DB and registers its OAuth client on <paramref name="host"/>.</summary>
    public async Task<(Guid Id, string Domain)> CreateTenantAsync(
        ShellHostFactory host,
        string? domain = null,
        bool isActive = true,
        bool isIsolatedDb = false,
        string? connectionString = null)
    {
        var tenantId = Guid.CreateVersion7();
        domain ??= $"tenant-{tenantId:N}.localhost";
        await using var connection = new NpgsqlConnection(CatalogConnectionString);
        await connection.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO catalog.tenants (id, name, is_active, is_isolated_db, connection_string) VALUES (@id, @name, @active, @isolated, @cs);" +
            "INSERT INTO catalog.tenant_domains (id, tenant_id, domain) VALUES (@domainId, @id, @domain);",
            connection))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("name", domain);
            cmd.Parameters.AddWithValue("active", isActive);
            cmd.Parameters.AddWithValue("isolated", isIsolatedDb);
            cmd.Parameters.AddWithValue("cs", (object?)connectionString ?? DBNull.Value);
            cmd.Parameters.AddWithValue("domainId", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("domain", domain);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Shell.Identity.Clients.TenantClientSynchronizer>()
            .SynchronizeAsync(CancellationToken.None);
        return (tenantId, domain);
    }

    /// <summary>Adds or updates a membership for the user with <paramref name="email"/> in <paramref name="tenantId"/>.</summary>
    public async Task AddMemberAsync(Guid tenantId, string email, string role = "member", string status = "active")
    {
        await using var connection = new NpgsqlConnection(CatalogConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO identity.tenant_memberships (tenant_id, user_id, role, status, created_at, updated_at)
            SELECT @tenantId, id, @role, @status, now(), now() FROM identity.users WHERE email = @email
            ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = EXCLUDED.role, status = EXCLUDED.status, updated_at = now()
            """,
            connection);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }
```
4. Add `<Using Include="Shell.Testing" />` to `tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj` so all existing test files resolve `PostgresFixture`, `ShellHostFactory`, `OidcTestClient`, `TestEmailSender`, `FakeSocialHandler`, and `Totp`.

- [x] **Step 3: Write a test in `tests/Shell.IntegrationTests/TenancyTests.cs` exercising `CreateTenantAsync` and `AddMemberAsync`**

Add `[Fact] public async Task Temporary_tenant_helper_provisions_oauth_client_and_membership_for_module_tests()` to `TenancyTests`:
```csharp
    [Fact]
    public async Task Temporary_tenant_helper_provisions_oauth_client_and_membership_for_module_tests()
    {
        await using var host = db.CreateHost();
        var (tenantId, domain) = await db.CreateTenantAsync(host);
        await db.AddMemberAsync(tenantId, DemoUserSeeder.Bob, role: "owner");

        var tokens = await new OidcTestClient(host).GetTokensAsync(DemoUserSeeder.Bob, DemoUserSeeder.Password, tenantId, domain);
        var response = await OidcTestClient.CallApiAsync(host, domain, HttpMethod.Get, "/api/products", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

- [x] **Step 4: Run build and tests to verify `Shell.Testing` compiles with zero warnings and all tests pass**

Run: `dotnet test --solution DigitalWorkspace.slnx`
Expected: PASS (0 warnings, 51/51 tests passing).

- [x] **Step 5: Commit**

```bash
git add Directory.Packages.props DigitalWorkspace.slnx tests/Shell.Testing/ tests/Shell.IntegrationTests/
git commit -m "test(testing): extract reusable Shell.Testing library with tenant helpers"
```

---

### Task 2: Implement `SmtpAccountEmailSender`, `EmailOptions`, and Loopback SMTP Integration Tests (`I1`)

**Files:**
- Modify: `src/Shell.Identity/ShellIdentityOptions.cs:40-81`
- Modify: `src/Shell.Identity/Email/AccountEmails.cs:1-42`
- Modify: `src/Shell.Identity/ShellIdentityExtensions.cs:50-60,210-217`
- Modify: `tests/Shell.Testing/PostgresFixture.cs:135-145`
- Modify: `tests/Shell.Testing/TestDoubles.cs`
- Modify: `tests/Shell.IntegrationTests/AccountSecurityTests.cs`

**Interfaces:**
- Consumes: `Shell.Identity.Email.IAccountEmailSender`, `System.Net.Mail.SmtpClient`, `System.Net.Mail.MailMessage`
- Produces:
  - `Shell.Identity.ShellIdentityOptions.EmailOptions` (`Host`, `Port`, `FromAddress`, `FromName`, `Username`, `Password`, `UseSsl`, `IsConfigured`)
  - `Shell.Identity.Email.SmtpAccountEmailSender : IAccountEmailSender`
  - `Shell.Testing.LoopbackSmtpServer : IAsyncDisposable`

- [x] **Step 1: Add `LoopbackSmtpServer` to `tests/Shell.Testing/TestDoubles.cs` and write failing SMTP integration tests in `tests/Shell.IntegrationTests/AccountSecurityTests.cs`**

1. In `tests/Shell.Testing/PostgresFixture.cs`, update `ShellHostFactory.ConfigureWebHost` so `TestEmailSender` is only registered when the test has not configured `Identity:Email:Host`:
```csharp
        builder.ConfigureTestServices(services =>
        {
            if (settings is null || !settings.TryGetValue("Identity:Email:Host", out var smtpHost) || string.IsNullOrWhiteSpace(smtpHost))
            {
                services.AddSingleton<IAccountEmailSender>(emails);
            }

            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FakeSocialHandler>(FakeSocialHandler.SchemeName, "Fake", _ => { });
        });
```
2. In `tests/Shell.Testing/TestDoubles.cs`, add `LoopbackSmtpServer` (`TcpListener` on `IPAddress.Loopback`, port `0`, capturing raw MIME messages received over SMTP and parsing `Subject`, `To`, and decoded links).
3. In `tests/Shell.IntegrationTests/AccountSecurityTests.cs`, add 2 `[Fact]` tests:
   - `Smtp_email_sender_delivers_password_reset_and_invitation_emails_with_usable_links`: starts `LoopbackSmtpServer`, creates a host with `["Identity:Email:Host"] = "127.0.0.1"`, `["Identity:Email:Port"] = smtp.Port.ToString(CultureInfo.InvariantCulture)`, `["Identity:Email:FromAddress"] = "noreply@auth.localhost"`, triggers `/account/forgot-password`, reads the reset link from `smtp.SingleMessageTo(email)` and resets the password; then sends an invitation for a tenant whose name is `"Acme <script>alert(1)</script>\r\nBcc: evil@example.com"` and verifies the SMTP subject has no `\r`/`\n` and the HTML body encodes `&lt;script&gt;` while keeping the invitation link valid.
   - `Invalid_smtp_options_fail_fast_at_startup_when_host_is_configured`: verifies `OptionsValidationException` is thrown when `Identity:Email:Host = "smtp.example.com"` and `Identity:Email:FromAddress = "not-an-email"` or `Identity:Email:Port = "70000"`.

- [x] **Step 2: Run integration tests to verify the new tests fail**

Run: `dotnet test --project tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj -- --filter-method "*Smtp*"`
Expected: FAIL (`LoggingAccountEmailSender` is used instead of SMTP because `SmtpAccountEmailSender` and `EmailOptions` do not exist yet).

- [x] **Step 3: Implement `EmailOptions` in `src/Shell.Identity/ShellIdentityOptions.cs`, `SmtpAccountEmailSender` in `src/Shell.Identity/Email/AccountEmails.cs`, and wire registration/validation in `src/Shell.Identity/ShellIdentityExtensions.cs`**

1. In `src/Shell.Identity/ShellIdentityOptions.cs`, add `public EmailOptions Email { get; set; } = new();` and nested class:
```csharp
    public sealed class EmailOptions
    {
        /// <summary>SMTP server host. Empty/null in local or test setups falls back to logging links to the console.</summary>
        public string? Host { get; set; }

        [Range(1, 65535)]
        public int Port { get; set; } = 587;

        public string FromAddress { get; set; } = "noreply@localhost";

        public string FromName { get; set; } = "Digital Workspace";

        public string? Username { get; set; }

        /// <summary>Secret: environment variable or secret store, never appsettings.</summary>
        public string? Password { get; set; }

        public bool UseSsl { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
    }
```
2. In `src/Shell.Identity/ShellIdentityExtensions.cs`:
   - Add `.Validate(o => !o.Email.IsConfigured || (o.Email.Port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(o.Email.FromAddress) && o.Email.FromAddress.Contains('@', StringComparison.Ordinal)), "Identity:Email requires a valid FromAddress and Port (1..65535) when Identity:Email:Host is set.")` to `services.AddOptions<ShellIdentityOptions>()`.
   - When `settings.Email.IsConfigured` is `true`, register `services.TryAddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();`, otherwise `services.TryAddSingleton<IAccountEmailSender, LoggingAccountEmailSender>();`.
3. In `src/Shell.Identity/Email/AccountEmails.cs`, implement `internal sealed class SmtpAccountEmailSender(IOptions<ShellIdentityOptions> options, ILogger<SmtpAccountEmailSender> logger) : IAccountEmailSender`:
   - Sanitizes subject lines by replacing `\r` and `\n` with spaces.
   - Builds a `MailMessage` with plain-text `Body` (`IsBodyHtml = false`) plus an `AlternateView` (`MediaTypeNames.Text.Html`) where dynamic values (`tenantName`, `role`, `link`) are encoded with `WebUtility.HtmlEncode`.
   - Sends using `using var client = new SmtpClient(emailOptions.Host!, emailOptions.Port) { EnableSsl = emailOptions.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network };` (setting `Credentials = new NetworkCredential(emailOptions.Username, emailOptions.Password)` when `Username` is non-empty).

- [x] **Step 4: Run integration tests to verify they pass**

Run: `dotnet test --project tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj -- --filter-method "*smtp*"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shell.Identity/ShellIdentityOptions.cs src/Shell.Identity/ShellIdentityExtensions.cs src/Shell.Identity/Email/AccountEmails.cs tests/Shell.Testing/PostgresFixture.cs tests/Shell.Testing/TestDoubles.cs tests/Shell.IntegrationTests/AccountSecurityTests.cs
git commit -m "feat(identity): add SMTP account email sender and options validation"
```

---

### Task 3: Wire Mailpit into Docker Compose, `appsettings`, and Documentation (`I1`, `B13`)

**Files:**
- Modify: `docker-compose.yml`
- Modify: `src/Host/appsettings.json`
- Modify: `docs/README.md`
- Modify: `docs/extension-points.md`
- Modify: `docs/module-guide.md`

**Interfaces:**
- Consumes: `ShellIdentityOptions.EmailOptions` (`Identity:Email:*`)
- Produces: Mailpit container in `docker-compose.yml` (`1025` SMTP, `8025` HTTP UI) and updated docs.

- [x] **Step 1: Add `mailpit` service and `Identity__Email__*` environment variables to `docker-compose.yml` and `src/Host/appsettings.json`**

1. In `docker-compose.yml`:
   - Under `x-host.environment`, add:
     ```yaml
     Identity__Email__Host: mailpit
     Identity__Email__Port: "1025"
     Identity__Email__FromAddress: noreply@auth.localhost
     Identity__Email__FromName: Digital Workspace
     Identity__Email__UseSsl: "false"
     ```
   - Under `services`, add:
     ```yaml
     mailpit:
       image: axllent/mailpit:v1.24
       ports:
         - "1025:1025" # SMTP
         - "8025:8025" # Web UI (http://localhost:8025)
     ```
2. In `src/Host/appsettings.json`, add `"Email"` under `"Identity"`:
   ```json
    "Email": {
      "Host": "",
      "Port": 587,
      "FromAddress": "noreply@localhost",
      "FromName": "Digital Workspace",
      "UseSsl": true
    },
   ```

- [x] **Step 2: Update `docs/README.md`, `docs/extension-points.md`, and `docs/module-guide.md`**

1. `docs/README.md`:
   - Mention Mailpit web UI at `http://localhost:8025` in the Docker Compose section.
   - Add `Shell.Testing/` under `tests/` in the directory tree.
   - Add `Identity:Email:Host`, `Port`, `FromAddress`, `FromName`, `Username`, `Password`, `UseSsl` to the configuration table.
2. `docs/extension-points.md`:
   - Update `## 1. Identity / phân quyền` to note that `SmtpAccountEmailSender` is built in and enabled whenever `Identity:Email:Host` is configured (or replaceable via `IAccountEmailSender`).
3. `docs/module-guide.md`:
   - Note in the testing section that module integration test projects can reference `tests/Shell.Testing` (`PostgresFixture`, `CreateTenantAsync`, `AddMemberAsync`, `OidcTestClient`).

- [x] **Step 3: Verify formatting, Release build, and full test suite**

Run:
1. `dotnet format DigitalWorkspace.slnx --verify-no-changes`
2. `dotnet build DigitalWorkspace.slnx -c Release`
3. `dotnet test --solution DigitalWorkspace.slnx -c Release --no-build`

Expected: All 3 commands exit with code `0` (`0 Warning(s)`, `0 Error(s)`, all tests passing).

- [x] **Step 4: Commit**

```bash
git add docker-compose.yml src/Host/appsettings.json docs/README.md docs/extension-points.md docs/module-guide.md docs/core-gap-analysis.md docs/superpowers/plans/2026-11-13-batch-1a-testing-and-email.md
git commit -m "chore(email): configure Mailpit in compose and document Shell.Testing and SMTP"
```
