# Kiến trúc

## Tổng quan

```mermaid
flowchart LR
    client([Client<br/>tenant-a.app.com / tenant-b.app.com]) --> nginx[Nginx<br/>round-robin]
    nginx --> host

    subgraph host[Host instance x N, stateless]
        direction TB
        sec[UseSecurityHeaders<br/>CSP, HSTS, X-Frame-Options] --> mw[TenantResolutionMiddleware<br/>domain → TenantInfo<br/>404 / 403]
        mw --> auth[UseShellIdentity<br/>AuthHostGuard · Bearer/Cookie<br/>TenantAccessMiddleware]
        auth --> ep[Endpoint của module & IAM<br/>/api/products · /iam/*]
        cache[(IMemoryCache<br/>tenant 10 phút · not found 1 phút<br/>membership 30 giây)] -.-> mw
        cache -.-> auth
        ep --> dbc[ProductsDbContext : TenantDbContext<br/>chọn connection string<br/>filter TenantId · gán TenantId]
    end

    mw -- cache miss --> catalog[(DB Catalog<br/>catalog.tenants · tenant_domains<br/>identity.users · memberships · openiddict)]
    dbc -- tenant Pool --> shared[(DB chung<br/>products.products<br/>có cột tenant_id)]
    dbc -- tenant Silo --> silo[(DB riêng tenant B<br/>products.products)]

    job[[dotnet Host.dll migrate<br/>advisory lock]] --> catalog
    job --> shared
    job --> silo
```

## Thành phần

| Project | Vai trò | Được tham chiếu bởi |
|---|---|---|
| `Shell.Abstractions` | `IModule`, `AddModuleDbContext`, `IMustHaveTenant`, `TenantInfo`, `ITenantContext`, `TenantDbContext` | Shell.Core, Shell.Identity, mọi module |
| `Shell.Core` | Nạp module; Catalog, phân giải tenant, cache; lệnh `migrate`; health check | Host, Shell.Identity |
| `Shell.Identity` | OpenIddict + ASP.NET Core Identity trên auth host (`/connect/*`, `/account/*`), kiểm tra bearer token + membership trên tenant host (`/iam/*`), CLI `disable-user`/`enable-user` | Host |
| `Host` | `Program.cs`: Serilog, security headers, pipeline, Swagger (dev), lệnh `migrate` & identity CLI | — |
| `Modules.X` | Nghiệp vụ của module X (internal) | Host (chỉ để copy DLL) |
| `Modules.X.Contracts` | DTO/interface công khai của X | Module khác cần dùng X |

**Quy tắc phụ thuộc** (kiểm tra bằng `tests/Shell.UnitTests/ArchitectureTests.cs`): module chỉ dùng module khác qua `Modules.Y.Contracts`; module không dùng `Shell.Core`/`Host`, chỉ `Shell.Abstractions`.

## Module

- Mỗi assembly `Modules.*.dll` có đúng một class `IModule` (`Id`, `DependsOn`, `ConfigureServices`, `MapEndpoints`).
- Lúc khởi động: `ModuleLoader` quét DLL → `ModuleCatalog` kiểm tra id, trùng id, dependency thiếu, vòng lặp → sắp xếp topo. Lỗi → log `Fatal`, exit 1.
- Endpoint của module nằm dưới `/api/{Id}`. Module luôn bật; "module" chỉ là ranh giới code và dữ liệu (schema riêng).

## Multi-tenant Hybrid

### Ba loại database

| DB | Nội dung | Kết nối |
|---|---|---|
| **Catalog** (master) | `catalog.tenants` (id, name, is_active, is_isolated_db, connection_string), `catalog.tenant_domains` (domain unique → tenant); schema `identity` (`users`, `tenant_memberships`, `invitations`, `audit_events`, OpenIddict, Data Protection keys) | Cố định: `ConnectionStrings:Catalog` |
| **Shared** (Pool) | Bảng của mọi module, dữ liệu của mọi tenant Pool, phân biệt bằng `tenant_id` | `ConnectionStrings:DefaultShared` |
| **Silo** (mỗi tenant một DB) | Cùng bảng như Shared, chỉ chứa dữ liệu một tenant | `tenants.connection_string` |

Mỗi DB Shared/Silo có đủ schema của mọi module (`products`, ...), mỗi schema có bảng `__ef_migrations_history` riêng.

### Luồng một request

1. `UseSecurityHeaders`: gắn `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy` (bỏ qua `/swagger`) và `Strict-Transport-Security` (khi `Identity:RequireHttps = true`).
2. `TenantResolutionMiddleware`: bỏ qua `/health`, `/swagger` và `PlatformHosts` (auth host).
3. Lấy domain: header `X-Tenant-Host` (chỉ khi `AllowHostHeaderOverride`) hoặc `Request.Host`, chuẩn hoá (chữ thường, bỏ port).
4. `TenantStore`: tra `IMemoryCache`; miss thì query Catalog, cache 10 phút. Không tìm thấy → cache "không có" 1 phút.
5. Không có tenant → **404**; `IsActive = false` → **403** (ProblemDetails).
6. Gán `TenantContext` (scoped) và thêm `TenantId` vào log scope.
7. `UseShellIdentity`: `UseRateLimiter` → `AuthHostGuardMiddleware` → `UseAuthentication` → `TenantAccessMiddleware` (đối chiếu claim `tenant_id` trong token với `ITenantContext.Current.Id` và kiểm tra trạng thái membership qua cache ngắn) → `UseAuthorization`.
8. Endpoint inject DbContext của module. `AddModuleDbContext` hỏi `ITenantConnectionStringProvider`: tenant Silo → connection string riêng (thiếu thì báo lỗi, **không** rơi về DB chung); tenant Pool → `DefaultShared`.
9. `TenantDbContext`:
   - **Đọc**: mọi entity `IMustHaveTenant` có filter `TenantId == tenant hiện tại`. Không có tenant → không thấy dòng nào.
   - **Ghi** (`SaveChanges`): `Added` → tự gán `TenantId`; `TenantId` khác tenant hiện tại → lỗi; `Modified/Deleted` đổi `TenantId` hoặc thuộc tenant khác → lỗi; chưa có tenant → `InvalidOperationException`.

Filter vẫn áp dụng trong DB Silo (an toàn hai lớp).

### Scale ngang

- Instance không giữ state ngoài cache có hạn (tenant). Nginx round-robin, không sticky session.
- Thay đổi trong Catalog lan tới mọi instance trong tối đa `CacheMinutes`.
- Npgsql pool connection theo từng connection string, nên mỗi DB Silo có pool riêng trong mỗi instance.

## Lệnh `migrate`

`dotnet Host.dll migrate` dùng cùng cấu hình và danh sách module với app, rồi:

1. Tạo DB Catalog nếu chưa có.
2. Lấy `pg_advisory_lock` trên DB Catalog (nhiều job chạy cùng lúc sẽ xếp hàng).
3. Migrate Catalog → seed tenant demo (nếu `SeedDemoTenants`).
4. Migrate DbContext của mọi module (theo thứ tự dependency) trên DB **Shared**.
5. Với từng tenant Silo: migrate các DbContext đó trên DB riêng (EF tự tạo DB nếu chưa có).

Không bao giờ chạy lúc khởi động app.

## Production readiness

| Mục | Cách làm |
|---|---|
| Bảo mật HTTP | Security headers (`nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `CSP`), `HSTS` khi `Identity:RequireHttps = true`, rate limit endpoint auth |
| Identity | OpenIddict (PKCE + refresh), host-only cookie trên auth host, Data Protection lưu DB, token gắn `tenant_id` |
| Health | `/health/live` (không check), `/health/ready` (Catalog + Shared, timeout 5s) |
| Lỗi | ProblemDetails cho 400/403/404/500, không lộ stack trace ngoài Development |
| Log | Serilog JSON ra console, có `TenantId` trong scope của request |
| Shutdown | SIGTERM → chờ tối đa `ShutdownTimeoutSeconds`; compose `stop_grace_period: 35s` |
| Container | Multi-stage, `aspnet:10.0`, user `app` (non-root) |
| Cấu hình | appsettings + biến môi trường, validate khi khởi động |
