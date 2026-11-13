# Batch 0 — Foundation & Guardrails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish CI, security headers, code analyzers (`TreatWarningsAsErrors`), automatic module architecture test discovery, and up-to-date documentation (`ADR-006`, `DigitalWorkspace.slnx`, `Shell.Identity`) before starting Batch 1 feature work.

**Architecture:** Add a lightweight security-headers middleware in `Host` driven by `ShellIdentityOptions.RequireHttps`, enforce `AnalysisLevel=latest-recommended` and `TreatWarningsAsErrors=true` in `Directory.Build.props` backed by a root `.editorconfig`, replace manual module references in `Shell.UnitTests.csproj` with wildcard discovery plus a source-tree completeness assertion in `ArchitectureTests`, update `docs/*` to match the `Shell.Identity` architecture, and wire everything into `.github/workflows/ci.yml`.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core Minimal API & Razor Pages, xUnit v3 (`Microsoft.Testing.Platform`), NetArchTest.Rules, Testcontainers.PostgreSql, Docker, GitHub Actions.

**Spec:** `docs/core-gap-analysis.md` (§9 Đợt 0: `E1`, `E2`, `I2`, `E4`, `E3`)

## Global Constraints

- Target framework: `net10.0` (`global.json` SDK `10.0.100`, runner `Microsoft.Testing.Platform`).
- Solution file name is `DigitalWorkspace.slnx` (never `Shell.slnx`).
- Module assemblies must never depend on `Shell.Core`, `Shell.Identity`, or `Shell.Host` (`ArchitectureTests`).
- All projects must build with `0 Warning(s)` and `0 Error(s)` under `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, and `TreatWarningsAsErrors=true`.

## Review Focus

1. **Inline `<style>` on Razor login pages under CSP (`I2`):** `src/Shell.Identity/Pages/Shared/_Layout.cshtml` uses an inline `<style>` block; `Content-Security-Policy` must allow `style-src 'self' 'unsafe-inline'` while keeping `default-src 'self'; frame-ancestors 'none'; form-action 'self'; object-src 'none'`.
2. **Local HTTP development & test hosts poisoned by HSTS (`I2`):** `Strict-Transport-Security` must only be emitted when `Identity:RequireHttps` is `true`, and omitted when `Identity:RequireHttps` is `false` (local demo/testing over HTTP).
3. **Swagger UI broken by strict CSP in Development (`I2`):** Requests under `/swagger` in Development use inline scripts; skip `Content-Security-Policy` on `/swagger` paths so developer API exploration still renders.
4. **Unreferenced module project in `src/Modules/` silently skipped by `ArchitectureTests` (`E4`):** If a new `src/Modules/<Name>/Modules.<Name>.csproj` exists on disk, `ArchitectureTests` must verify that every `Modules.*.csproj` under `src/Modules/` is loaded into `ModuleAssemblies` or `ContractAssemblies`.
5. **RFC 6238 TOTP `HMACSHA1` and xUnit snake_case test names under `AnalysisLevel=latest-recommended` (`E3`):** Authenticator apps require SHA-1 per RFC 6238 (`CA5350` in `TestDoubles.Totp`) and Razor Pages handlers require instance `OnGet` methods (`CA1822`); scoping rules in `.editorconfig` and targeted fixes must keep `TreatWarningsAsErrors=true` green without weakening production code checks.

---

### Task 1: Auto-discover all `src/Modules/*` in `Shell.UnitTests` & `ArchitectureTests` (`E4`)

**Files:**
- Modify: `tests/Shell.UnitTests/Shell.UnitTests.csproj:17-22`
- Modify: `tests/Shell.UnitTests/ArchitectureTests.cs:13-76`

**Interfaces:**
- Consumes: `Shell.Core.Modules.ModuleLoader.AssemblyPrefix` (`"Modules."`), `ModuleLoader.ContractsSuffix` (`".Contracts"`), `ModuleLoader.DiscoverAssemblies(string directory)`
- Produces: `ArchitectureTests.Every_module_project_in_source_tree_is_loaded()`

- [x] **Step 1: Add the source-tree completeness test to `tests/Shell.UnitTests/ArchitectureTests.cs`**

Add `[Fact] public void Every_module_project_in_source_tree_is_loaded()` to `ArchitectureTests`:

```csharp
    [Fact]
    public void Every_module_project_in_source_tree_is_loaded()
    {
        var modulesRoot = FindModulesSourceDirectory();
        var projectNames = Directory.EnumerateFiles(modulesRoot, $"{ModuleLoader.AssemblyPrefix}*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var loadedNames = ModuleAssemblies.Concat(ContractAssemblies)
            .Select(a => a.GetName().Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(projectNames);
        Assert.Equal(projectNames, loadedNames);
    }

    private static string FindModulesSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Modules");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate 'src/Modules' from AppContext.BaseDirectory.");
    }
```

- [x] **Step 2: Replace the hardcoded `Modules.Products` reference in `tests/Shell.UnitTests/Shell.UnitTests.csproj` with a wildcard**

Update lines 17–21 of `tests/Shell.UnitTests/Shell.UnitTests.csproj` so any future `src/Modules/*/*.csproj` is automatically referenced and copied to the test output directory:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Shell.Core\Shell.Core.csproj" />
    <!-- Automatically reference all module and contract projects so new modules can never be skipped by ArchitectureTests. -->
    <ProjectReference Include="..\..\src\Modules\*\*.csproj" />
  </ItemGroup>
```

- [x] **Step 3: Run unit tests to verify discovery and completeness pass**

Run: `dotnet test --project tests/Shell.UnitTests/Shell.UnitTests.csproj`
Expected: PASS (all `ArchitectureTests` and `ModuleCatalogTests` succeed).

- [x] **Step 4: Commit**

```bash
git add tests/Shell.UnitTests/Shell.UnitTests.csproj tests/Shell.UnitTests/ArchitectureTests.cs
git commit -m "test(arch): auto-reference all modules and verify source tree coverage"
```

---

### Task 2: Security Headers Middleware & HSTS (`I2`)

**Files:**
- Create: `src/Host/SecurityHeadersExtensions.cs`
- Modify: `src/Host/Program.cs:77-96`
- Modify: `tests/Shell.IntegrationTests/AuthenticationTests.cs:170-184`

**Interfaces:**
- Consumes: `Shell.Identity.ShellIdentityOptions.RequireHttps`
- Produces: `Shell.Host.SecurityHeadersExtensions.UseSecurityHeaders(this WebApplication app) -> IApplicationBuilder`

- [x] **Step 1: Write failing integration tests in `tests/Shell.IntegrationTests/AuthenticationTests.cs`**

Add two `[Fact]` tests to `AuthenticationTests` exercising:
1. Default security headers (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy`) on both `/account/login` (auth host) and `/api/products` (tenant host), and absence of `Strict-Transport-Security` when `Identity:RequireHttps = false`.
2. Presence of `Strict-Transport-Security: max-age=31536000; includeSubDomains` when `Identity:RequireHttps = true`, and omission of `Content-Security-Policy` on `/swagger` paths so Swagger UI scripts are not blocked.

```csharp
    [Fact]
    public async Task Responses_include_clickjacking_and_sniffing_protection_headers()
    {
        await using var host = db.CreateHost();
        var authClient = host.CreateClient(new() { BaseAddress = new Uri($"http://{ShellHostFactory.AuthHost}") });
        var apiClient = host.CreateClient(new() { BaseAddress = new Uri($"http://{TenantA}") });

        var loginResponse = await authClient.GetAsync("/account/login", TestContext.Current.CancellationToken);
        var apiResponse = await apiClient.GetAsync("/api/products", TestContext.Current.CancellationToken);

        foreach (var response in new[] { loginResponse, apiResponse })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("style-src 'self' 'unsafe-inline'", csp);
            Assert.Contains("form-action 'self'", csp);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        }
    }

    [Fact]
    public async Task Hsts_is_emitted_when_https_is_required_and_swagger_skips_csp()
    {
        await using var host = db.CreateHost(new Dictionary<string, string?>
        {
            ["Identity:RequireHttps"] = "true",
        });
        var authClient = host.CreateClient(new() { BaseAddress = new Uri($"https://{ShellHostFactory.AuthHost}") });

        var loginResponse = await authClient.GetAsync("/account/login", TestContext.Current.CancellationToken);
        Assert.Equal("max-age=31536000; includeSubDomains", loginResponse.Headers.GetValues("Strict-Transport-Security").Single());

        var swaggerResponse = await authClient.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        Assert.Equal("nosniff", swaggerResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(swaggerResponse.Headers.Contains("Content-Security-Policy"));
    }
```

- [x] **Step 2: Run integration tests to verify the new tests fail**

Run: `dotnet test --project tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj -- --filter-method "*Responses_include_clickjacking*"`
Expected: FAIL (`X-Content-Type-Options` header not found).

- [x] **Step 3: Implement `Shell.Host.SecurityHeadersExtensions.UseSecurityHeaders(this WebApplication app)` in `src/Host/SecurityHeadersExtensions.cs` and wire it in `src/Host/Program.cs`**

Create `src/Host/SecurityHeadersExtensions.cs`:
- Namespace: `Shell.Host`
- Signature: `public static IApplicationBuilder UseSecurityHeaders(this WebApplication app)`
- Resolves `IOptions<ShellIdentityOptions>` from `app.Services`.
- On every request (`app.Use`), sets:
  - `X-Content-Type-Options` = `"nosniff"`
  - `X-Frame-Options` = `"DENY"`
  - `Referrer-Policy` = `"strict-origin-when-cross-origin"`
  - If `!context.Request.Path.StartsWithSegments("/swagger")`, sets `Content-Security-Policy` = `"default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'"`
  - If `identityOptions.Value.RequireHttps` is `true`, sets `Strict-Transport-Security` = `"max-age=31536000; includeSubDomains"`
- In `src/Host/Program.cs`, call `app.UseSecurityHeaders();` immediately after `app.UseForwardedHeaders();` (line 77).

- [x] **Step 4: Run integration tests to verify they pass**

Run: `dotnet test --project tests/Shell.IntegrationTests/Shell.IntegrationTests.csproj`
Expected: PASS (all existing auth/tenancy/IAM tests plus the 2 new security header tests pass).

- [x] **Step 5: Commit**

```bash
git add src/Host/SecurityHeadersExtensions.cs src/Host/Program.cs tests/Shell.IntegrationTests/AuthenticationTests.cs
git commit -m "feat(host): add security headers and conditional HSTS middleware"
```

---

### Task 3: Code Style, `.editorconfig`, and Roslyn Analyzers (`E3`)

**Files:**
- Create: `.editorconfig`
- Modify: `Directory.Build.props:1-8`
- Modify: `tests/Shell.UnitTests/ModuleCatalogTests.cs:85`
- Modify: `tests/Shell.IntegrationTests/PostgresFixture.cs:67`
- Modify: `tests/Shell.IntegrationTests/TestDoubles.cs:71-75`
- Modify: `tests/Shell.IntegrationTests/TenantDbContextTests.cs:8`

**Interfaces:**
- Consumes: none
- Produces: Clean zero-warning build with `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`.

- [x] **Step 1: Enable `AnalysisLevel`, `EnforceCodeStyleInBuild`, and `TreatWarningsAsErrors` in `Directory.Build.props` and verify build fails on unaddressed warnings**

Update `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Run: `dotnet build DigitalWorkspace.slnx --no-incremental`
Expected: FAIL with analyzer errors (`CA1707`, `CA1848`, `CA1873`, `CA1861`, `CA1822`, `CA1305`, `CA5350`, `CA1816`, `CA1711`, `CA1859`).

- [x] **Step 2: Create root `.editorconfig` with project conventions and scoped analyzer severities**

Create `.editorconfig` at the repository root:

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

[*.{csproj,props,targets,slnx,json,yml,yaml}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
csharp_prefer_braces = true:warning
# Standard structured logging is used across the solution instead of LoggerMessage source-gen delegates.
dotnet_diagnostic.CA1848.severity = none
dotnet_diagnostic.CA1873.severity = none

# EF Core migrations are tool-generated.
[**/Migrations/*.cs]
generated_code = true

# Razor Page handlers must be instance methods for ASP.NET Core page routing.
[**/Pages/**/*.cshtml.cs]
dotnet_diagnostic.CA1822.severity = none

# Test projects use snake_case Fact/Theory names, xUnit *Collection suffix conventions, and instance fixture helpers.
[tests/**/*.cs]
dotnet_diagnostic.CA1707.severity = none
dotnet_diagnostic.CA1711.severity = none
dotnet_diagnostic.CA1822.severity = none
```

- [x] **Step 3: Fix the remaining 4 code warnings in `tests/`**

Apply these exact fixes:
1. `tests/Shell.UnitTests/ModuleCatalogTests.cs:85`: change `private static IModule Module(` to `private static FakeModule Module(`.
2. `tests/Shell.IntegrationTests/PostgresFixture.cs:67`: change `Convert.ToInt64(await command.ExecuteScalarAsync())` to `Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)`.
3. `tests/Shell.IntegrationTests/TestDoubles.cs:71-75`: wrap `HMACSHA1.HashData` with `#pragma warning disable CA5350 // RFC 6238 TOTP mandates HMAC-SHA1` and `#pragma warning restore CA5350`, and change `.ToString("D6")` to `.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)`.
4. `tests/Shell.IntegrationTests/TenantDbContextTests.cs:8`: change `public class TenantDbContextTests` to `public sealed class TenantDbContextTests` (satisfies `CA1816`).

- [x] **Step 4: Run full build and unit tests to verify zero warnings and zero errors**

Run: `dotnet build DigitalWorkspace.slnx --no-incremental`
Expected: Build succeeded with `0 Warning(s)` and `0 Error(s)`.

- [x] **Step 5: Commit**

```bash
git add .editorconfig Directory.Build.props tests/Shell.UnitTests/ModuleCatalogTests.cs tests/Shell.IntegrationTests/PostgresFixture.cs tests/Shell.IntegrationTests/TestDoubles.cs tests/Shell.IntegrationTests/TenantDbContextTests.cs
git commit -m "build: add .editorconfig and enforce recommended analyzers as errors"
```

---

### Task 4: Documentation Alignment & ADR-006 Identity (`E2`)

**Files:**
- Modify: `docs/README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/extension-points.md`
- Modify: `docs/module-guide.md`
- Modify: `docs/adr.md`

**Interfaces:**
- Consumes: `ShellIdentityOptions`, `ShellIdentityExtensions`, `DigitalWorkspace.slnx`
- Produces: Synchronized documentation in `docs/`

- [x] **Step 1: Update `docs/README.md` to reflect `DigitalWorkspace.slnx` and `Shell.Identity`**

In `docs/README.md`:
- Line 68: replace `dotnet test --solution Shell.slnx` with `dotnet test --solution DigitalWorkspace.slnx`.
- Lines 23–25 & 58: note that `migrate` also migrates `identity` schema on Catalog DB and seeds demo users (`alice@demo.local`, `bob@demo.local`) and OAuth clients for demo tenants when `SeedDemoTenants = true`.
- Lines 73–92 (`Cấu trúc thư mục`): add `Shell.Identity/` (OpenIddict authorization server, ASP.NET Core Identity, Razor Pages `/account/*`, IAM `/iam/*`, `TenantAccessMiddleware`, CLI `disable-user`/`enable-user`).
- Lines 96–104 (`Endpoint`): add auth host endpoints (`/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/endsession`, `/account/*`) and tenant IAM endpoints (`/iam/members`, `/iam/invitations`, `/iam/audit`).
- Lines 109–119 (`Cấu hình`): add `Identity:Issuer`, `Identity:RequireHttps`, `Identity:AccessTokenMinutes`, `Identity:RefreshTokenDays`, `Identity:MembershipCacheSeconds`, `Identity:RateLimitPerMinute`, `Identity:Clients:RedirectUris`, `Identity:Keys:Path`, `Identity:Keys:AutoGenerate`, `Identity:Google:*`, `Identity:Microsoft:*`, and `Shell:TrustForwardedHeaders`.
- Line 122 (`Lưu ý vận hành`): remove `⚠️ Chưa có xác thực...` and replace with a concise summary of the implemented auth model (auth host vs tenant host, `tenant-member` / `tenant-admin` policies, CLI `disable-user`/`enable-user`).

- [x] **Step 2: Update `docs/architecture.md`, `docs/extension-points.md`, and `docs/module-guide.md`**

1. In `docs/architecture.md`:
   - Add `Shell.Identity` to the `Thành phần` table (`OpenIddict + Identity trên auth host, kiểm tra bearer token + membership trên tenant host, /iam/*`, referenced by `Host`).
   - In `Ba loại database` → **Catalog**: mention schema `identity` (`users`, `tenant_memberships`, `invitations`, `audit_events`, OpenIddict tables, Data Protection keys).
   - In `Luồng một request`: add step between `TenantResolutionMiddleware` and module endpoints for `UseShellIdentity` (`AuthHostGuardMiddleware` → `UseAuthentication` → `TenantAccessMiddleware` verifying token `tenant_id` claim + active membership in `IMemoryCache`/Catalog → `UseAuthorization`) and `UseSecurityHeaders`.
2. In `docs/extension-points.md`:
   - Replace `## 1. Identity / phân quyền` ("Bắt buộc trước production. Hiện tại ai gọi đúng domain...") with what remains to extend on top of `Shell.Identity`: module-level permissions (`B7`), machine-to-machine client credentials (`I5`), and real email sender (`I1`, replacing `LoggingAccountEmailSender` via `services.AddSingleton<IAccountEmailSender, ...>()`).
3. In `docs/module-guide.md`:
   - Replace all 3 occurrences of `Shell.slnx` (lines 123, 161, 165, 176) with `DigitalWorkspace.slnx`.
   - In `## 5. Đưa module vào solution và Host`: note that `Shell.UnitTests.csproj` automatically includes `src/Modules/*/*.csproj` via wildcard, so only `DigitalWorkspace.slnx` and `src/Host/Host.csproj` need explicit registration.

- [x] **Step 3: Add `ADR-006` and update `ADR-003` in `docs/adr.md`**

In `docs/adr.md`:
- In `ADR-003` (`Hệ quả`), replace `(-) Chưa có xác thực: domain quyết định tenant. Phải thêm auth ràng buộc user ↔ tenant trước production.` with `(+) Xác thực và ràng buộc user ↔ tenant được xử lý ở tầng Shell.Identity (xem ADR-006).`
- Append `## ADR-006: Identity tập trung ở Catalog DB, tách Auth Host và gắn Token vào Tenant`:
  - **Bối cảnh:** Một tài khoản cá nhân có thể tham gia nhiều tenant (giống Slack/GitHub), nhưng access token cấp ra phải bị khoá chặt vào đúng một tenant và có thể thu hồi quyền truy cập nhanh khi bị xoá khỏi tenant hoặc khoá tài khoản.
  - **Quyết định:**
    - Đặt `AuthDbContext` (schema `identity`: users, memberships, invitations, audit_events, OpenIddict, DataProtection keys) trong **Catalog DB**.
    - Các trang đăng nhập Razor (`/account/*`) và endpoint OIDC (`/connect/*`) chỉ phục vụ trên **auth host** (`Identity:Issuer`), dùng host-only cookie (`__Host-shell.session`).
    - Mỗi tenant có OAuth 2.1 public client (Authorization Code + PKCE). Access token mang claim `tenant_id`; trên các domain tenant, `TenantAccessMiddleware` đối chiếu claim này với `ITenantContext.Current.Id` và kiểm tra trạng thái membership (cache ngắn `MembershipCacheSeconds`, mặc định 30s).
  - **Hệ quả:**
    - `(+) Một tài khoản dùng chung cho nhiều tenant, cookie đăng nhập không bao giờ rò rỉ sang subdomain của tenant.`
    - `(+) Token của tenant A đem gọi sang domain của tenant B bị từ chối (403); thu hồi membership có hiệu lực tối đa sau MembershipCacheSeconds.`
    - `(-) Catalog DB chịu thêm tải đọc khi kiểm tra membership lúc hết TTL cache (30s) và khi đăng nhập.`

- [x] **Step 4: Verify no stale `Shell.slnx` or "Chưa có xác thực" remains outside `core-gap-analysis.md`**

Run: `git grep -n "Shell.slnx" docs/`
Expected: Only `docs/core-gap-analysis.md` (which documents the historical gap) matches.

- [x] **Step 5: Commit**

```bash
git add docs/README.md docs/architecture.md docs/extension-points.md docs/module-guide.md docs/adr.md DigitalWorkspace.slnx docs/core-gap-analysis.md
git commit -m "docs: sync architecture, module guide, and ADR-006 with Shell.Identity"
```

---

### Task 5: GitHub Actions CI Pipeline (`E1`)

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `DigitalWorkspace.slnx`, `Dockerfile`, `global.json`
- Produces: Automated CI workflow triggered on `push` (`main`, `develop`) and `pull_request`

- [x] **Step 1: Create `.github/workflows/ci.yml`**

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main, develop]

env:
  DOTNET_NOLOGO: true
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore dependencies
        run: dotnet restore DigitalWorkspace.slnx

      - name: Check formatting
        run: dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore

      - name: Build (warnings as errors)
        run: dotnet build DigitalWorkspace.slnx -c Release --no-restore

      - name: Run unit & integration tests
        run: dotnet test --solution DigitalWorkspace.slnx -c Release --no-build

      - name: Build Docker image
        run: docker build -t shell:ci .
```

- [x] **Step 2: Run the exact CI commands locally to verify formatting, Release build, tests, and Docker build pass**

Run:
1. `dotnet format DigitalWorkspace.slnx --verify-no-changes` (if it reports formatting diffs, run `dotnet format DigitalWorkspace.slnx` first and inspect `git diff`)
2. `dotnet build DigitalWorkspace.slnx -c Release`
3. `dotnet test --solution DigitalWorkspace.slnx -c Release --no-build`
4. `docker build -t shell:ci .`

Expected: All 4 commands exit with code `0`.

- [x] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add GitHub Actions workflow for format, build, test, and docker"
```
