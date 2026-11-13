# Rà soát core & backlog bổ sung

> Rà soát ngày 2026-11-13, trên commit `f73fb8e` (nhánh `develop`).
> Mục đích: ghi lại những phần **core** còn thiếu để lên kế hoạch bổ sung sau. Đây là backlog, không phải thiết kế: khi làm từng mục thì viết ADR/thiết kế riêng.

**Mức ưu tiên**

| Mức | Ý nghĩa |
|---|---|
| **P0** | Phải có trước khi chạy production / nhận tenant thật |
| **P1** | Nên có trước khi viết module nghiệp vụ thật thứ hai (nếu không, mỗi module sẽ tự làm một kiểu) |
| **P2** | Cần khi hệ thống lớn lên (nhiều tenant, nhiều team, nhiều tích hợp) |

**Công sức ước lượng**: S (≤ 1 ngày) · M (vài ngày) · L (≥ 1 tuần).

---

## 1. Tóm tắt đánh giá

Nền móng **kiến trúc** đã tốt và có test: module loader, tenancy Pool/Silo, `TenantDbContext`, lệnh `migrate`, authorization server (OpenIddict + Identity), token gắn với tenant, IAM cơ bản, audit log đăng nhập, health check, Docker.

Cái còn thiếu nằm ở ba nhóm:

1. **Vận hành nền tảng (platform)**: chưa có cách tạo/quản lý tenant ngoài việc `INSERT` SQL tay rồi chạy `migrate`; chưa có platform admin.
2. **Building blocks cho module**: module mới phải tự làm audit field, phân trang, validation, quyền chi tiết, event, background job... Hiện chỉ có một module mẫu (`Products`) nên chưa lộ ra, nhưng sẽ lộ ngay khi có module thứ hai.
3. **Hạ tầng kỹ thuật & quy trình**: chưa có CI, observability (tracing/metrics), security headers, CORS, gửi email thật, dọn dữ liệu định kỳ; tài liệu đã lệch so với code.

### Đã có (không cần làm lại)

| Mảng | Có gì |
|---|---|
| Module | `IModule`, quét `Modules.*.dll`, topo sort, fail fast; architecture test (NetArchTest) |
| Tenancy | Phân giải theo domain + cache (kể cả negative cache); Pool/Silo; filter + stamp `TenantId`; 404/403 |
| Migration | Lệnh `migrate` riêng, advisory lock, migrate DB chung + từng DB Silo, `IMigrationStep` |
| Identity | OpenIddict (code + PKCE, refresh), cookie host-only, MFA, lockout, social login (Google/Microsoft), quên mật khẩu, lời mời, rate limit endpoint đăng nhập, disable/enable user (CLI), Data Protection lưu DB |
| Phân quyền | Policy `tenant-member` / `tenant-admin`, token bị từ chối ở tenant khác, thu hồi membership có hiệu lực nhanh |
| IAM | `/iam/members`, `/iam/invitations`, `/iam/audit` |
| Vận hành | Health live/ready, Serilog JSON, graceful shutdown, Forwarded headers, Docker multi-stage non-root, compose 2 instance + Nginx |
| Test | Unit + integration (Testcontainers) cho tenancy, auth, IAM, bảo mật tài khoản |

---

## 2. Quản lý tenant & nền tảng

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **T1** | **Provisioning tenant** | ✅ Đã hoàn thành ở Đợt 1B (`TenantProvisioningService`, `MigrationRunner.MigrateTenantAsync`, `TenantClientSynchronizer.SynchronizeTenantAsync`, `TenantStore.Invalidate`, CLI `create-tenant`, xác nhận email `/account/confirm-email` và self sign-up `/tenants/new`). | Đã hoàn thành. | P0 | M |
| **T2** | **Platform admin (super admin)** | Không có vai trò cấp nền tảng. Thao tác hệ thống duy nhất là CLI `disable-user`/`enable-user`. | Vai trò platform admin (claim/bảng riêng), khu vực quản trị trên auth host hoặc host admin riêng: danh sách tenant, khoá/mở, domain, user. | P0 | M |
| **T3** | Bảo mật connection string Silo | Lưu plain text trong `catalog.tenants.connection_string` (đã ghi trong extension-points). | Lưu tên secret, resolve qua secret store (Key Vault/Vault/AWS SM) trong `TenantStore`; ít nhất mã hoá bằng Data Protection. | P0 (nếu có Silo thật) | M |
| **T4** | Vô hiệu cache tenant tức thời | Khoá tenant/đổi domain mất tới `CacheMinutes` (10') mới có hiệu lực trên mọi instance. | Postgres `LISTEN/NOTIFY` hoặc Redis pub/sub → xoá cache key. Cần khi có T1/T2. | P1 | S–M |
| **T5** | Vòng đời tenant | Chưa có xoá/archive tenant, export dữ liệu tenant, script chuyển Pool → Silo (test hiện chỉ đổi cờ, không chuyển dữ liệu). | Quy trình offboarding (xoá dữ liệu mọi schema theo `tenant_id`, xoá DB Silo), export, script copy Pool → Silo. | P2 | L |
| **T6** | Feature flag / gói dịch vụ theo tenant | Module luôn bật cho mọi tenant. | Bảng `tenant_features` trong Catalog, đưa vào `TenantInfo`; helper `RequireFeature("x")` cho endpoint group. | P2 | M |
| **T7** | Cài đặt tenant | Không có timezone, ngôn ngữ, tiền tệ, branding theo tenant. | `tenant_settings` (key/value có schema) + `ITenantSettings` trong Abstractions. | P2 | M |

## 3. Building blocks cho module (Shell.Abstractions / Shell.Core)

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **B1** | Audit field tự động | `Product.CreatedBy` gán tay trong endpoint; không có `CreatedAt/UpdatedAt/UpdatedBy`. | Interface `IAuditable` + `SaveChangesInterceptor` (hoặc trong `TenantDbContext`) dùng `ICurrentUser` + `TimeProvider`. | P1 | S |
| **B2** | Soft delete | Chỉ có hướng dẫn "dùng filter có tên". | `ISoftDelete` + named filter `SoftDelete` tự gắn trong `TenantDbContext`, chuyển `Deleted` → `Modified`. | P1 | S |
| **B3** | Optimistic concurrency | Không có (ngoài bảng Identity). Hai request sửa cùng bản ghi → ghi đè im lặng. | Dùng `xmin` của Postgres làm concurrency token; map `DbUpdateConcurrencyException` → 409 ProblemDetails. | P1 | S |
| **B4** | Phân trang / sắp xếp / lọc | `GET /api/products` trả **toàn bộ** bảng, không giới hạn. | Kiểu chung `PageRequest`/`PagedResult<T>` (ưu tiên keyset/cursor cho bảng lớn), giới hạn page size tối đa. | P1 | S–M |
| **B5** | Quy ước validation | `ProductEndpoints.Create` tự dựng `Dictionary<string,string[]>`. | Chọn một cách: built-in validation của Minimal API (.NET 10) hoặc FluentValidation + endpoint filter; trả `ValidationProblem` thống nhất. | P1 | S |
| **B6** | Quy ước lỗi nghiệp vụ | ProblemDetails có cho 400/403/404/500, nhưng chưa có mã lỗi nghiệp vụ chuẩn (`type`/`code`) để client xử lý. | Danh mục error code + exception/result nghiệp vụ map sang ProblemDetails (có `traceId`). | P2 | S |
| **B7** | **Phân quyền chi tiết theo module** | Chỉ có 3 vai trò tenant (`owner`/`admin`/`member`) và 2 policy. Module không khai báo được quyền kiểu `products.write`. | Module khai báo permission trong `IModule` (hoặc Contracts); gán permission cho role (cố định trước, tuỳ biến theo tenant sau); `RequirePermission(...)`. | P1 | M–L |
| **B8** | Giao tiếp giữa module bằng event + Outbox | Mới là extension point trong docs. | `IEventPublisher`/`IEventHandler<T>` in-process trước; Outbox theo schema module + dispatcher duyệt DB chung và mọi DB Silo. | P1 | L |
| **B9** | **Background job theo tenant** | Không có `BackgroundService`/`IHostedService` nào. Module muốn chạy job phải tự duyệt tenant và set `TenantContext` (vốn là kiểu của `Shell.Core`, module không được dùng). | Abstraction `ITenantJobRunner` / `ForEachTenantAsync(scope => ...)` trong Abstractions; lịch chạy định kỳ (PeriodicTimer, hoặc Quartz/Hangfire nếu cần persistence), khoá phân tán để chỉ một instance chạy. | P1 | M |
| **B10** | Seed dữ liệu tham chiếu của module | `IMigrationStep` nằm trong `Shell.Core` nên module không dùng được; `IModule` không có hook seed. | Hook `SeedAsync` (per-DB / per-tenant) mà lệnh `migrate` gọi sau khi migrate module. | P1 | S |
| **B11** | Cache dữ liệu nghiệp vụ theo tenant | Chỉ có `IMemoryCache` cho tenant. | `HybridCache` (+ Redis khi nhiều instance) với helper tự thêm `TenantId` vào key. | P2 | S–M |
| **B12** | Lưu trữ file theo tenant | Không có. | `IFileStorage` (local / S3 / Azure Blob), đường dẫn có tiền tố tenant, URL ký có hạn. | P2 | M |
| **B13** | Test kit cho module | Fixture (`PostgresFixture`, `OidcTestClient`) nằm trong `Shell.IntegrationTests`, module khác không tái dùng được. | Tách thành project `Shell.Testing` (host factory, lấy token cho user/tenant, tạo tenant tạm). | P1 | S–M |
| **B14** | Đa ngôn ngữ (i18n) | Trang đăng nhập và thông báo lỗi chỉ có một ngôn ngữ. | `IStringLocalizer` + resource, chọn culture theo user/tenant (gắn với T7). | P2 | M |

## 4. Identity & bảo mật

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **I1** | **Gửi email thật** | `LoggingAccountEmailSender` chỉ ghi link ra log → lời mời, quên mật khẩu không dùng được thật. | Implementation SMTP/SendGrid/SES + template; outbox/retry để không mất mail. | P0 | S–M |
| **I2** | Security headers cho trang đăng nhập & API | Không có HSTS, CSP, `X-Frame-Options`/`frame-ancestors`, `X-Content-Type-Options`, `Referrer-Policy`. Trang login có thể bị nhúng iframe (clickjacking). | Middleware header (hoặc ở Nginx); `UseHsts` khi `RequireHttps`. | P0 | S |
| **I3** | Dọn token/authorization hết hạn | Không có job prune OpenIddict → bảng `openiddict_tokens`/`authorizations` tăng mãi. | Job định kỳ gọi `PruneAsync` của token/authorization manager (dựa trên B9). | P1 | S |
| **I4** | Trang tài khoản tự phục vụ | Có login/register/MFA/logins/forgot password; chưa có đổi mật khẩu khi đang đăng nhập, đổi email, cập nhật hồ sơ, xem/thu hồi phiên & thiết bị, xoá tài khoản. | Bổ sung các trang Razor tương ứng + audit event. | P1 | M |
| **I5** | Client máy-máy / tích hợp | Mỗi tenant chỉ có một OAuth client (authorization code cho SPA). Không có client credentials hay API key cho tích hợp hệ thống ngoài. | Client credentials gắn tenant (do admin tenant tạo trong `/iam`), scope giới hạn. | P2 | M |
| **I6** | Rate limit phân tán | Rate limit theo IP **trên từng instance** → N instance = N lần hạn mức. | Đưa về Nginx/gateway, hoặc limiter dùng Redis. | P2 | S |
| **I7** | Retention audit log | `identity.audit_events` không bao giờ xoá; chỉ có audit cho sự kiện identity/IAM, không có audit cho thao tác nghiệp vụ. | Chính sách lưu giữ (partition theo tháng + xoá cũ); cân nhắc audit trail nghiệp vụ chung (gắn với B1/B8). | P2 | M |
| **I8** | Xoay vòng khoá ký token | Hỗ trợ nhiều cert nhưng thêm/xoá file thủ công. | Runbook + cảnh báo trước khi cert hết hạn; có thể tự sinh cert mới khi gần hết hạn. | P2 | S |

## 5. HTTP / API

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **A1** | CORS | Không cấu hình. Cấu hình dev trỏ redirect về SPA ở `{domain}:5173` trong khi API ở `:5080` → khác origin, trình duyệt sẽ chặn. | Policy CORS theo domain của tenant (origin phải thuộc tenant đang phân giải), hoặc quyết định SPA luôn cùng origin qua reverse proxy. | P1 | S |
| **A2** | Rate limit cho API nghiệp vụ | Chỉ có cho endpoint đăng nhập. | Partition theo `TenantId` (+ user) để một tenant không làm nghẽn tenant khác. | P1 | S |
| **A3** | OpenAPI cho production / sinh client | Swashbuckle chỉ bật ở Development. | Cân nhắc `Microsoft.AspNetCore.OpenApi` built-in; xuất file OpenAPI lúc build để sinh client TypeScript cho frontend. | P2 | S |
| **A4** | API versioning | Không có. | Chỉ cần khi có client bên ngoài; quyết định quy ước (URL `/api/v1/...` hay header) trước khi API bị dùng rộng. | P2 | S |
| **A5** | Giới hạn request | Không đặt timeout/kích thước body riêng. | `AddRequestTimeouts`, giới hạn body (đặc biệt khi có upload B12). | P2 | S |

## 6. Observability

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **O1** | **Tracing & metrics** | Không có OpenTelemetry. | OTel: ASP.NET Core, HttpClient, Npgsql/EF Core; tag `tenant.id`, `module`; exporter OTLP; metrics theo tenant (request, lỗi, latency). | P1 | M |
| **O2** | Tương quan log ↔ request | Log có `TenantId`, `UserId` trong scope. Serilog 4 và `AddProblemDetails` thường đã ghi trace id, nhưng chưa có test/kiểm chứng và chưa trả correlation id qua header. | Xác nhận log JSON có `@tr`/`@sp` và ProblemDetails có `traceId`; trả header (vd. `traceparent`/`X-Request-Id`) để support tra cứu. | P2 | S |
| **O3** | Gom log tập trung, cảnh báo, error tracking | Log JSON ra console (đúng hướng container), chưa có sink/alert. | Chọn stack (Seq / Loki / ELK / Sentry...) + cảnh báo cơ bản (5xx, DB down, cert sắp hết hạn). | P2 | M |
| **O4** | Giám sát DB Silo | Readiness cố ý không check Silo. Một DB Silo chết thì không ai biết đến khi tenant báo. | Job kiểm tra định kỳ từng DB Silo → metric/alert (không ảnh hưởng readiness). | P2 | S |

## 7. Dữ liệu & vận hành

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **D1** | Quản lý secret | Connection string, mật khẩu PFX, client secret social login qua env/appsettings. | Tích hợp secret store qua configuration provider; tài liệu hoá biến môi trường bắt buộc cho production. | P0 | S–M |
| **D2** | Backup / restore | Chưa có chiến lược. Restore một tenant Pool từ DB chung là khó. | Runbook: backup Catalog + DB chung + từng DB Silo, PITR; thử restore định kỳ; cách khôi phục dữ liệu một tenant. | P0 | M |
| **D3** | Hạ tầng triển khai production | Chỉ có `docker-compose.yml` demo (HTTP, cert tự sinh, header override bật, Nginx trả `X-Upstream`). | Manifest production (K8s/Helm hoặc compose prod): TLS, wildcard domain + custom domain, migrate Job, probes, resource limits. | P1 | M |
| **D4** | Migrate song song nhiều Silo | `MigrationRunner` chạy tuần tự. | `Parallel.ForEachAsync` có giới hạn, hoặc job theo từng tenant; báo cáo tenant nào lỗi mà không chặn tenant khác. | P2 | S |
| **D5** | Review migration | Migration áp trực tiếp bằng `MigrateAsync`. | Sinh script idempotent / migration bundle để review trước; checklist expand → contract. | P2 | S |

## 8. Quy trình kỹ thuật (DevEx)

| ID | Thiếu gì | Hiện trạng | Đề xuất | Ưu tiên | Công sức |
|---|---|---|---|---|---|
| **E1** | **CI** | Không có pipeline (`.github/workflows`...). | Build + unit + integration (runner có Docker) + kiểm tra format + build image; chặn merge khi đỏ. | P0 | S |
| **E2** | **Tài liệu lệch so với code** | `docs/README.md` vẫn ghi "⚠️ Chưa có xác thực"; `architecture.md`, cấu trúc thư mục và bảng cấu hình không nhắc `Shell.Identity` / mục `Identity:*`; `extension-points.md` §1 đã làm xong; README và `module-guide.md` dùng `Shell.slnx` nhưng file thật là `DigitalWorkspace.slnx`. | Cập nhật docs; thêm ADR cho quyết định Identity (OpenIddict, auth host riêng, token gắn tenant, dữ liệu identity nằm ở Catalog). | P0 | S |
| **E3** | Code style & analyzer | Chỉ `nullable` là lỗi; không có `.editorconfig`. | `.editorconfig`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors` trong CI. | P1 | S |
| **E4** | Tạo module bằng template | Tạo module = copy `Products` + đổi tên tay 5 bước; quên thêm reference vào `Shell.UnitTests` thì architecture test **vẫn xanh nhưng bỏ qua module mới**. | `dotnet new shell-module` template; và/hoặc cho architecture test tự quét mọi `Modules.*.dll` (như `ModuleLoader`) để không thể bỏ sót. | P1 | S–M |
| **E5** | Frontend / client mẫu | Redirect URI trỏ về SPA (`{domain}:5173/signin-callback`) nhưng repo không có client nào → khó thử luồng đăng nhập end-to-end bằng tay. | Quyết định frontend nằm ở đâu (repo này hay repo riêng); tối thiểu một SPA mẫu đăng nhập + gọi API. | P1 | M |
| **E6** | README gốc, quy ước repo | Không có `README.md` ở thư mục gốc, không có changelog/quy ước commit/branch, không có Dependabot/Renovate. | README gốc trỏ vào `docs/`; Dependabot cho NuGet + Docker image. | P2 | S |

---

## 9. Thứ tự đề xuất

Gom theo đợt, mỗi đợt là một kế hoạch riêng:

1. **Đợt 0 – Dọn nền (nhanh, rẻ):** `E1` CI, `E2` docs, `I2` security headers, `E4` (phần `ArchitectureTests` tự quét `Modules.*.dll`), `E3` `.editorconfig` & analyzer.
2. **Đợt 1 – Vận hành & Onboarding tenant** (chia thành 3 kế hoạch nhỏ làm lần lượt):
   - **Đợt 1A – Hạ tầng Test & Email:** `B13` tách project `Shell.Testing` → `I1` gửi email thật (SMTP + Mailpit dev).
   - **Đợt 1B – Provisioning & Self Sign-up Tenant:** ✅ Đã hoàn thành (`T1`: tách migrate/sync đơn tenant, CLI `create-tenant`, xác minh email tài khoản và luồng khách tự tạo Pool tenant dùng thử tại `/tenants/new`).
   - **Đợt 1C – Quản trị nền tảng & Đồng bộ Cache:** `T2` Platform Admin (Minimal API `/admin/*` trên auth host + CLI `grant-admin`/`revoke-admin`) → `T4` vô hiệu cache tenant/membership tức thời qua Postgres `LISTEN/NOTIFY`.
3. **Đợt 2 – Building blocks cho module** (làm trước khi viết module thật thứ hai): `B1`, `B2`, `B3`, `B4`, `B5`, `B10`, `E4` (template tạo module); sau đó `B7` phân quyền chi tiết (kiểm tra theo permission, ánh xạ từ role cố định và mở rộng được sang custom role).
4. **Đợt 3 – Bất đồng bộ & vận hành dài hạn:** `B9` background job → `I3` prune token, `O4` giám sát DB Silo; `B8` event + outbox.
5. **Đợt 4 – Trước production:** `D1` + `T3` quản lý secret & bảo vệ connection string Silo, `A1` CORS động theo tenant + `A2` rate limit + `E5` SPA mẫu verify E2E, `I4` trang tài khoản tự phục vụ, `O1` observability, `D3` hạ tầng triển khai (`docker-compose.prod.yml` + TLS), `D2` backup/restore.
6. **Sau đó (P2):** phần còn lại (`T5`, `T6`, `T7`, `I5`–`I8`, `B6`, `B11`, `B12`, `B14`, `A3`–`A5`, `O2`, `O3`, `D4`, `D5`, `E6`) theo nhu cầu thực tế.

## 10. Quyết định đã chốt (2026-11-13)

- **Tạo tenant & Quản trị nền tảng (`T1`, `T2`, `T6`):** Khách **tự đăng ký (self sign-up)** công khai theo luồng **xác minh email trước rồi mới tạo tenant** (đăng ký/đăng nhập → xác nhận link email → tạo Pool tenant dùng thử tại `/tenants/new` để chống rác DB và OAuth client), nâng cấp gói sau. `T2` (Platform Admin) triển khai dưới dạng **Minimal API JSON (`/admin/*` trên auth host, đồng nhất với `/iam/*`)** kết hợp lệnh CLI `grant-admin`/`revoke-admin` để đội vận hành kiểm soát/khoá/hỗ trợ; `T6` (gói/feature flag) và `T5` (chuyển Pool → Silo) giữ ở `P2`.
- **Phân quyền (`B7`):** Endpoint của module kiểm tra theo **permission** (`RequirePermission(...)`); tầng giải quyết quyền ban đầu ánh xạ từ **bộ role cố định** nhưng thiết kế sẵn abstraction để mở rộng sang custom role per-tenant sau này mà không sửa code module.
- **Frontend & Client (`A1`, `I5`, `E5`):** SPA chạy **tách domain riêng** → `A1` (CORS động theo domain của tenant đang phân giải) là bắt buộc; API giữ xác thực Bearer token qua OpenIddict để về lâu dài mở rộng sang mobile app và `I5` (client máy-máy) mà không phải đổi kiến trúc auth.
- **Hạ tầng triển khai (`D1`, `T3`, `D3`):** Giai đoạn production đầu tiên triển khai trên **VM + Docker Compose**, mở rộng lên Cloud/K8s sau → `D3` tập trung vào `docker-compose.prod.yml`, TLS (wildcard + custom domain), migrate job và resource limits; `D1`/`T3` dùng cơ chế secret/mã hoá tại host qua abstraction để dễ thay bằng Cloud secret store sau.
- **Pháp lý & vòng đời dữ liệu (`T5`, `I7`, `D2`):** Chưa bắt buộc ngay khi ra mắt → `T5` (offboarding/export) và `I7` (retention audit log) giữ ở `P2`; ưu tiên `D2` (backup/restore runbook) trước production.
