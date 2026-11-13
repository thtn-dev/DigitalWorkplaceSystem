# Shell — modular monolith multi-tenant (.NET 10)

Bộ khung để xây hệ thống nghiệp vụ **multi-tenant** dạng **modular monolith**:

- **Module** = một mảng nghiệp vụ tách riêng trong code (project, DbContext, schema, migration riêng), khai báo bằng một class `IModule`.
- **Multi-tenant Hybrid**: mỗi tenant hoặc dùng **DB chung** (Pool, cách ly bằng `TenantId` + global query filter) hoặc **DB riêng** (Silo, cách ly bằng connection string). Module không phải viết dòng code nào về tenant.
- Tenant xác định theo **domain** của request (`tenant1.app.com`, `shop.khachhang.com`), có cache.
- Scale ngang: instance stateless, migration chạy bằng lệnh riêng, health check, log JSON, Docker.

Đọc tiếp: [architecture.md](architecture.md) · [module-guide.md](module-guide.md) · [adr.md](adr.md) · [extension-points.md](extension-points.md) · [core-gap-analysis.md](core-gap-analysis.md) (backlog phần core còn thiếu)

## Yêu cầu

- .NET SDK 10.0
- Docker (cho `docker compose` và integration test)

## Quick start

### Chạy cả cụm (2 instance + Nginx + Postgres)

```bash
docker compose up --build -d
```

Thứ tự: `postgres` → `migrate` (tạo/migrate DB Catalog + schema `identity`, seed 2 tenant demo, 2 tài khoản demo `alice@demo.local` / `bob@demo.local` và OAuth client khi bật `SeedDemoTenants`, migrate DB chung và DB riêng của tenant B, rồi thoát) → `host1`, `host2` → `nginx` tại http://localhost:8080, kèm `mailpit` (SMTP `1025`, giao diện web xem mail tại http://localhost:8025).

Hai tenant demo:

| Domain | Kiểu | Dữ liệu nằm ở |
|---|---|---|
| `tenant-a.localhost` | Pool | DB `shell_shared` (chung) |
| `tenant-b.localhost` | Silo | DB `shell_shared_tenant_b` (riêng) |

```bash
# Chọn tenant bằng header X-Tenant-Host ...
curl -X POST localhost:8080/api/products -H "X-Tenant-Host: tenant-a.localhost" \
     -H "Content-Type: application/json" -d '{"name":"A-Laptop","price":1200}'
# ... hoặc bằng Host header thật (giống production)
curl -X POST localhost:8080/api/products -H "Host: tenant-b.localhost" \
     -H "Content-Type: application/json" -d '{"name":"B-Phone","price":800}'

curl localhost:8080/api/products -H "X-Tenant-Host: tenant-a.localhost"   # chỉ thấy A-Laptop
curl localhost:8080/api/products -H "X-Tenant-Host: tenant-b.localhost"   # chỉ thấy B-Phone
curl -i localhost:8080/api/products -H "X-Tenant-Host: unknown.localhost" # 404

# Kiểm tra dữ liệu nằm ở DB nào
docker compose exec postgres psql -U shell -d shell_shared          -c "select name, tenant_id from products.products"
docker compose exec postgres psql -U shell -d shell_shared_tenant_b -c "select name, tenant_id from products.products"

docker compose down -v   # dọn dẹp (xoá cả dữ liệu)
```

### Chạy local + Swagger

```bash
docker compose up -d postgres                  # Postgres ở localhost:5432
dotnet tool restore                            # dotnet-ef (local tool)
dotnet run --project src/Host -- migrate       # tạo DB shell_dev_*, seed 2 tenant, rồi thoát
dotnet run --project src/Host                  # http://localhost:5080/swagger
```

Trong Swagger UI, mỗi endpoint có ô **X-Tenant-Host**: nhập `tenant-a.localhost` hoặc `tenant-b.localhost`.
Môi trường Development dùng DB riêng (`shell_dev_catalog`, `shell_dev_shared`, `shell_dev_shared_tenant_b`) để không lẫn với cụm docker compose.

### Test

```bash
dotnet test --solution DigitalWorkspace.slnx     # unit + architecture + integration (cần Docker)
```

## Cấu trúc thư mục

```
src/
  Host/                    Program.cs (pipeline, SecurityHeadersExtensions, lệnh migrate & identity CLI, Swagger), appsettings
  Shell.Abstractions/      Thứ duy nhất module tham chiếu từ shell:
    IModule.cs               hợp đồng module
    ModuleDbContextExtensions.cs  AddModuleDbContext (chọn DB theo tenant)
    Tenancy/                 IMustHaveTenant, TenantInfo, ITenantContext, TenantDbContext
  Shell.Core/
    Modules/                 ModuleLoader, ModuleCatalog (thứ tự dependency, fail fast)
    Tenancy/                 CatalogDbContext, TenantStore (cache), TenantResolutionMiddleware, DemoTenantSeeder
    Data/                    MigrationRunner (lệnh migrate), DatabaseHealthCheck
  Shell.Identity/          Authorization server (OpenIddict + Identity trên auth host),
                           Razor Pages (/account/*), Bearer validation + TenantAccessMiddleware,
                           IAM (/iam/*), audit log, Data Protection keys, CLI disable-user/enable-user
  Modules/
    Products/                Module mẫu: Product (IMustHaveTenant), ProductsDbContext, /api/products
    Products.Contracts/      DTO công khai cho module khác
tests/
  Shell.Testing/           Test kit dùng lại (`PostgresFixture`, `ShellHostFactory`, `OidcTestClient`, `LoopbackSmtpServer`, helper tạo tenant/member tạm)
  Shell.UnitTests/         ModuleCatalog + architecture test (NetArchTest, auto-scan Modules.*.csproj)
  Shell.IntegrationTests/  Testcontainers: quy tắc TenantDbContext, migrate, Pool vs Silo, auth, IAM, bảo mật
deploy/nginx.conf          Load balancer cho docker compose
Dockerfile, docker-compose.yml, Directory.Build.props, Directory.Packages.props, .editorconfig, dotnet-tools.json
```

## Endpoint

| Endpoint | Host / Tenant | Mô tả |
|---|---|---|
| `GET /health/live` | Mọi host | Process còn sống |
| `GET /health/ready` | Mọi host | DB Catalog và DB chung trả lời, lỗi → 503 |
| `/swagger` | Mọi host (chỉ Development) | Swagger UI |
| `/connect/*`, `/account/*` | **Chỉ Auth host** (`Identity:Issuer`) | OIDC Authorization Code + PKCE, đăng nhập, MFA, lời mời, quên mật khẩu |
| `/iam/members`, `/iam/invitations`, `/iam/audit` | **Tenant host** (policy `tenant-admin`) | Quản lý thành viên, lời mời, audit log đăng nhập của tenant |
| `/api/{moduleId}/...` | **Tenant host** (cần token + membership) | Endpoint nghiệp vụ, ví dụ `/api/products` |

Request cần tenant: domain không có trong Catalog → **404**, tenant bị khoá (`is_active = false`) → **403**.

## Cấu hình

`appsettings.json` → `appsettings.{Environment}.json` → biến môi trường (`__` thay cho `:`). Sai cấu hình → Host dừng ngay khi khởi động.

| Khoá | Mặc định | Ý nghĩa |
|---|---|---|
| `ConnectionStrings:Catalog` | *(bắt buộc)* | DB master: tenant, domain, connection string Silo, schema `identity` |
| `ConnectionStrings:DefaultShared` | *(bắt buộc)* | DB chung của mọi tenant Pool (mỗi module một schema) |
| `Tenancy:AllowHostHeaderOverride` | `false` | Cho phép header `X-Tenant-Host` thay Host thật. **Chỉ bật khi dev/demo** |
| `Tenancy:CacheMinutes` | 10 | Cache tenant đã tìm thấy (mỗi instance) |
| `Tenancy:NotFoundCacheSeconds` | 60 | Cache domain không tồn tại (chống spam DB) |
| `Tenancy:SeedDemoTenants` | `false` | Lệnh `migrate` tạo 2 tenant demo + tài khoản demo + OAuth client nếu trống |
| `Identity:Issuer` | *(bắt buộc)* | URL công khai của authorization server (vd: `https://auth.app.com/`) |
| `Identity:RequireHttps` | `true` | Đặt `false` chỉ cho local/demo HTTP; khi `true` bật `__Host-` cookie và HSTS |
| `Identity:AccessTokenMinutes` | 10 | Thời hạn access token (phút) |
| `Identity:RefreshTokenDays` | 14 | Thời hạn refresh token (ngày) |
| `Identity:MembershipCacheSeconds` | 30 | Cache kiểm tra membership trên API instance (trễ tối đa khi thu hồi quyền) |
| `Identity:RateLimitPerMinute` | 60 | Giới hạn request/phút/IP cho endpoint đăng nhập & OIDC trên mỗi instance |
| `Identity:Clients:RedirectUris` | `["https://{domain}/signin-callback"]` | Mẫu redirect URI cho OAuth client của từng tenant |
| `Identity:Keys:Path` | `"keys"` | Thư mục chứa cert ký/mã hoá token (`signing-*.pfx`, `encryption-*.pfx`) |
| `Identity:Keys:AutoGenerate` | `false` | Tự sinh cert self-signed khi trống (chỉ dùng cho dev/demo) |
| `Identity:Email:Host` | *(trống)* | Máy chủ SMTP gửi mail xác thực/lời mời (để trống = chỉ ghi link ra log; khi đặt phải có `FromAddress` và `Port` hợp lệ) |
| `Identity:Email:Port` | `587` | Cổng SMTP (`1..65535`; Mailpit dev dùng `1025`) |
| `Identity:Email:FromAddress` / `FromName` | `noreply@localhost` / `Digital Workspace` | Địa chỉ và tên hiển thị người gửi email |
| `Identity:Email:Username` / `Password` | *(trống)* | Thông tin đăng nhập SMTP (nếu máy chủ SMTP yêu cầu xác thực) |
| `Identity:Email:UseSsl` | `true` | Bật TLS/SSL khi kết nối SMTP (`false` cho Mailpit dev nội bộ) |
| `Identity:Google:*` / `Microsoft:*` | *(trống)* | `ClientId` / `ClientSecret` cho social login (chỉ bật khi cấu hình đủ) |
| `Shell:ShutdownTimeoutSeconds` | 30 | Thời gian chờ request đang chạy khi nhận SIGTERM |
| `Shell:TrustForwardedHeaders` | `false` | Tin tưởng `X-Forwarded-For` / `X-Forwarded-Proto` từ reverse proxy |
| `Serilog:MinimumLevel:Default` | Information | Log ra console dạng JSON |

## Lưu ý vận hành

- **Xác thực & cách ly tenant:** `Shell.Identity` cấp access token gắn `tenant_id` qua Authorization Code + PKCE trên Auth host (`Identity:Issuer`). Các tenant host xác thực Bearer token và kiểm tra membership đang hoạt động (`tenant-member` / `tenant-admin`). Dùng `dotnet Host.dll disable-user <email>` / `enable-user <email>` để khoá/mở tài khoản khẩn cấp.
- Thêm/khoá tenant hoặc đổi domain trong Catalog: mỗi instance thấy thay đổi sau tối đa `CacheMinutes` (domain mới bị 404 trước đó: tối đa `NotFoundCacheSeconds`).
- Thêm tenant Silo: tạo dòng trong Catalog rồi chạy `migrate`. Lệnh này tự tạo DB riêng và migrate mọi module trên DB đó.
- Migration **không** tự chạy khi khởi động: chạy `dotnet Host.dll migrate` (hoặc `docker run <image> migrate`) trước khi deploy.
- Khi `migrate` tạo database mới, EF Core log vài dòng `Failed executing DbCommand` / `An error occurred using the connection...`: đó là lúc EF kiểm tra DB và bảng lịch sử *trước khi tạo*. Migration vẫn thành công, lần sau không còn.
- Connection string của tenant Silo đang lưu dạng plain text trong Catalog. Xem phần secret store trong [extension-points.md](extension-points.md).
