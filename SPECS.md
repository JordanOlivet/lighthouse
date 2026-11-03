# Lighthouse - Technical Specifications

## 1. Overview

Lighthouse is a web application for managing Docker containers and Docker Compose projects. It gives one interface to view, control, update and monitor a Docker host.

### Architecture

The application is deployed as a **single unified container** that holds:
- **Backend**: .NET 10 Web API
- **Frontend**: SvelteKit static build
- **Reverse proxy**: Nginx
- **Process manager**: Supervisor

### Key Features

- Container and compose project management (start, stop, restart, up, down, logs, stats)
- Automatic compose file discovery, plus an in-app editor for compose and `.env` files
- Image update detection for containers and compose projects, with manual, bulk and scheduled (cron) updates
- Scheduled pruning of unused images
- Application self-update from GitHub releases, with a maintenance mode
- Update notifications through Discord and/or a generic HTTP webhook (Apprise-compatible)
- Real-time UI updates via Server-Sent Events (SSE)
- Users, groups, roles and per-resource permissions
- Operation history, plus an application log viewer for admins
- Password reset by email
- UI in English, French and Spanish

---

## 2. Application Architecture

### Unified Deployment

The production deployment uses a single Docker image managed by Supervisor:

```
┌─────────────────────────────────────────────┐
│           Unified Container                 │
│  ┌───────────────────────────────────────┐  │
│  │            Supervisor                 │  │
│  │  ┌─────────────┐  ┌────────────────┐  │  │
│  │  │   Nginx     │  │   .NET API     │  │  │
│  │  │   (:80)     │  │   (:5050)      │  │  │
│  │  └──────┬──────┘  └───────┬────────┘  │  │
│  │         │                 │           │  │
│  │         │    /api/*       │           │  │
│  │         └────────────────►│           │  │
│  └───────────────────────────┼───────────┘  │
│                              │              │
│                              ▼              │
│                    ┌─────────────────┐      │
│                    │  SQLite + Docker│      │
│                    │     Socket      │      │
│                    └─────────────────┘      │
└─────────────────────────────────────────────┘
```

Nginx serves the frontend and proxies `/api/*` to the backend. The image also includes the Docker CLI and the compose plugin, which compose operations use.

### Backend (.NET 10)

Layered architecture:

```
src/
├── Controllers/         → API endpoints, HTTP request/response handling
├── Services/            → Business logic and Docker operations
│   ├── Notifications/   → Discord + generic webhook channels
│   ├── Registry/        → Registry clients (Docker Hub, GHCR, generic OCI)
│   ├── LogStreaming/    → Container/compose/app log history and live tails
│   ├── Email/           → Email providers (Mock, Resend)
│   └── Security/        → Password hashing, security stamp cache
├── BackgroundServices/  → Periodic cleanup
├── DTOs/                → Data transfer objects
├── Models/              → Domain entities
├── Data/                → EF Core context and migrations
├── Middleware/          → Error handling, security headers, rate limiting, log enrichment
├── Configuration/       → Options classes for appsettings
├── Validators/          → FluentValidation validators
├── Filters/             → Action filters
├── Extensions/          → DI registration (ServiceCollectionExtensions), helpers
├── Exceptions/          → Domain exception hierarchy (AppException + subclasses)
├── Constants/           → Shared constants (ErrorCodes)
└── Utils/               → Utility classes
```

`Program.cs` stays thin. Service registrations live in per-subsystem extension methods in `Extensions/ServiceCollectionExtensions.cs`.

**Key Services:**

| Service | Responsibility |
|---------|---------------|
| `DockerService` | Docker daemon communication via Docker.DotNet |
| `ComposeService` / `ComposeOperationService` | Docker Compose operations (up, down, start, stop, restart) |
| `ComposeFileScannerService` / `ComposeDiscoveryService` | Filesystem discovery of compose files |
| `ComposeFileCacheService` | Thread-safe in-memory cache of discovered files |
| `ConflictResolutionService` | Project name conflict detection |
| `ProjectMatchingService` | Match running Docker projects with compose files |
| `ComposeCommandClassifierService` | Decide which commands need a compose file |
| `ComposeFileEditorService` | Read/write a project's compose file and adjacent `.env` |
| `ComposeHostPathService` / `PathMappingService` | Run compose from the host path so relative paths resolve as with the host CLI |
| `ComposeEnvFileResolver` | Re-apply a global `--env-file` when recreating projects |
| `ImageDigestService` / `ComposeUpdateChecker` | Detect image updates by comparing digests with the registry |
| `ComposeUpdateService` / `ContainerUpdateService` | Pull and recreate projects/containers |
| `RegistryCredentialService` | Registry logins (stored in the Docker config) |
| `SelfUpdateService` / `GitHubReleaseService` | Application self-update from GitHub releases |
| `SelfFilterService` | Hide Lighthouse's own containers/project and block operations on them |
| `CrashLoopDetectionService` | Detect crash-looping containers from Docker events |
| `SseConnectionManagerService` | Manage SSE connections and broadcast events |
| `OperationService` | Track long-running operations (history, cancel, acknowledge) |
| `AuthService` / `JwtTokenService` / `UserService` | Authentication, tokens, user CRUD |
| `PermissionService` | Role, group and per-resource permission checks |
| `CompositeNotificationService` | Fan out notifications to Discord and the generic webhook |
| `AppLogService` | Application log history (CLEF files) and live tail |
| `LogLevelService` | Change the Serilog minimum level at runtime |

**Hosted / Background Services:**

| Service | Responsibility |
|---------|---------------|
| `ComposeDiscoveryInitializer` | Initial compose file scan on startup |
| `DockerEventsMonitorService` | Listen to the Docker event stream and push SSE updates |
| `ProjectUpdateCheckBackgroundService` | Periodic update check of compose projects |
| `AutoUpdateComposeBackgroundService` | Cron auto-update of compose projects (default `0 2 * * *`, disabled by default) |
| `AutoUpdateAppBackgroundService` | Cron self-update of the application (default `0 2 * * *`, disabled by default) |
| `AutoPruneImagesBackgroundService` | Cron prune of unused images (default `0 3 * * *`, disabled by default) |
| `AppUpdateNotificationStartupService` | Send the "update completed" notification after a self-update restart |
| `CleanupBackgroundService` | Remove expired reset tokens, operations older than 7 days, expired sessions |

**Database Schema (SQLite):**

| Table | Description |
|-------|-------------|
| `Users` | User accounts (BCrypt-hashed passwords) |
| `Roles` | Role definitions |
| `UserGroups` / `UserGroupMemberships` | User groups and their members |
| `ResourcePermissions` | Per-container / per-project permissions for a user or a group |
| `Sessions` | Refresh tokens (stored as hashes) |
| `PasswordResetTokens` | Password reset tokens |
| `Operations` | Operation history (updates, compose actions, ...) |
| `AppSettings` | Runtime settings (key-value) |

### Frontend (SvelteKit)

SvelteKit application with Svelte 5 runes:

```
src/
├── routes/
│   ├── (protected)/     → dashboard, containers, compose, images, logs-app,
│   │                      settings, users, user-groups, permissions, dev
│   ├── login/ forgot-password/ reset-password/ change-password/ add-email/
├── lib/
│   ├── components/      → Reusable UI components (bits-ui based)
│   ├── stores/          → Svelte 5 runes stores
│   ├── api/             → API client functions (Axios)
│   ├── types/           → TypeScript definitions
│   ├── utils/           → Utilities
│   └── i18n/            → Translations (en, fr, es)
└── app.html             → HTML template
```

### Compose Discovery System

**Automatic file discovery:**

1. Scans a root directory (`/app/compose-files` by default) recursively
2. Discovers all `.yml` and `.yaml` files that contain a `services:` key
3. Extracts project names (from the `name` field, the directory, or the filename)
4. Caches results in memory with a configurable TTL

**Configuration:**

```json
{
  "ComposeDiscovery": {
    "RootPath": "/app/compose-files",
    "ScanDepthLimit": 5,
    "CacheDurationSeconds": 300,
    "MaxFileSizeKB": 1024
  }
}
```

**Compose extensions read by Lighthouse:**

| Extension | Level | Effect |
|-----------|-------|--------|
| `x-disabled: true` | root | Hide the file from discovery |
| `x-auto-update: false` | root | Skip the project during scheduled auto-updates (default: `true`) |
| `x-update-policy: disabled` | service or root | Ignore available updates for that service/project |

```yaml
x-disabled: true
name: my-project
services:
  web:
    image: nginx
```

**Conflict Resolution:**

When multiple files share a project name:
- 1 active file → Project uses that file
- 0 active files → Project hidden
- 2+ active files → Conflict error displayed

**Command Classification:**

| Requires File | Works Without File |
|---------------|-------------------|
| `up`, `build`, `pull`, `push`, `config` | `start`, `stop`, `restart`, `pause`, `unpause` |
| | `logs`, `ps`, `top`, `down`, `rm`, `kill` |

### Notifications

Two independent channels. Each one is enabled on its own, and both are sent concurrently when enabled. A failing channel never blocks the other, nor the update that triggered it.

| Channel | Settings keys | Format |
|---------|---------------|--------|
| Discord | `NotificationsDiscordEnabled`, `NotificationsDiscordWebhookUrl` | Discord embeds |
| Generic webhook | `NotificationsWebhookEnabled`, `NotificationsWebhookUrl`, `NotificationsWebhookHeaders` | JSON `POST` |

The generic webhook payload works with the Apprise API (`/notify/{key}`):

```json
{
  "title": "🐳 Compose auto-update — 2 updated, 0 failed",
  "body": "plain-text summary",
  "type": "info | success | warning | failure",
  "event": "app.update.started | app.update.failed | app.update.completed | compose.autoupdate | images.prune | test",
  "timestamp": "2026-11-03T12:00:00Z",
  "data": { "...": "event-specific structured data" }
}
```

Headers are entered free-form, one `Name: Value` per line (e.g. `Authorization: Bearer …`). They are write-only: the settings API returns them masked and they are never logged. `Content-Type`, `Content-Length` and `Host` cannot be overridden. Requests time out after 10 s.

### Real-Time Events (SSE)

Endpoint: `GET /api/events/stream`. Event types:

`ContainerStateChanged`, `ComposeProjectStateChanged`, `OperationUpdate`, `PullProgressUpdate`, `ProjectUpdatesChecked`, `ContainerUpdatesChecked`, `ImagesChanged`, `MaintenanceMode`, `Periodic`.

---

## 3. Technical Stack

### Backend

| Component | Technology |
|-----------|------------|
| Framework | .NET 10 (ASP.NET Core) |
| ORM | Entity Framework Core 10 |
| Database | SQLite |
| Docker Client | Docker.DotNet + Docker CLI / compose plugin |
| Authentication | JWT bearer, BCrypt for passwords |
| Validation | FluentValidation |
| Scheduling | Cronos (cron expressions) |
| Real-time | Server-Sent Events (SSE) |
| Logging | Serilog (console + CLEF files) |
| YAML | YamlDotNet |
| Testing | xUnit, Moq, FluentAssertions, EF Core InMemory |

### Frontend

| Component | Technology |
|-----------|------------|
| Framework | SvelteKit 3 |
| UI Framework | Svelte 5 (runes) |
| Language | TypeScript 6 |
| Build Tool | Vite 8 |
| Components | bits-ui 2 |
| Styling | Tailwind CSS 4, tailwind-variants |
| Icons | lucide-svelte |
| Data Fetching | TanStack Svelte Query 6 |
| API Client | Axios |
| Validation | Zod 4 |
| Code Editor | Monaco Editor |
| Toasts | svelte-sonner |
| i18n | i18next |
| Testing | Vitest |

### Deployment

| Component | Technology |
|-----------|------------|
| Container | Docker |
| Process Manager | Supervisor |
| Reverse Proxy | Nginx |
| Base Image | `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (frontend built with `node:26-alpine`) |
| Registry | `ghcr.io/jordanolivet/lighthouse` |

---

## 4. Security

### Docker Socket Access

The application needs Docker socket access, which grants **root-level access** to the host. Security measures:

- Compose files are resolved server-side from the project name; clients never send file paths
- Path traversal prevention with `PathValidatorService`, all paths checked against `RootPath`
- Lighthouse's own containers and project are hidden and protected from operations
- Every operation is recorded in the `Operations` history, and application logs carry the acting user

### Authentication

**JWT Secret:**

The `Jwt__Secret` setting is **required** for the application to start. The secret signs and verifies the JSON Web Tokens that authenticate users.

| Aspect | Details |
|--------|---------|
| Purpose | Cryptographically sign authentication tokens |
| Minimum Length | 32 characters |
| Algorithm | HMAC-SHA256 |
| Storage | Environment variable (never in code/config files) |

**Generation:**
```bash
# Linux/Mac
openssl rand -base64 32

# PowerShell (Windows)
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

**Security considerations:**
- Changing the secret invalidates all existing sessions
- Use unique secrets per environment

**Token Strategy:**

| Token Type | Lifetime (default) | Storage |
|------------|-------------------|---------|
| Access Token | 15 minutes (`Jwt:ExpirationMinutes`) | Frontend `localStorage` |
| Refresh Token | 1 day, or 30 days with "remember me" (`Jwt:RefreshExpirationDays[Extended]`) | HttpOnly cookie (`SameSite=Lax`, `Secure` over HTTPS); hash in the `Sessions` table |

**Token Flow:**
1. Login → access token + refresh cookie issued
2. Access token expires → frontend refreshes it (on 401, and proactively before expiry)
3. Refresh token expires → re-authentication required
4. Logout / password change → refresh tokens revoked

**Password Security:**
- BCrypt hashing, work factor 12 (`Security:PasswordHashing:WorkFactor`)
- Minimum 8 characters
- Forced password change on first login (`MustChangePassword`)
- Password reset by email (Mock or Resend provider); tokens expire after 15 minutes

### Authorization

- **Roles**: `admin` has full access. Admin-only areas: users, roles, settings, notifications, images, registries, app logs, self-update, and image updates.
- **Per-resource permissions**: granted to a user or a group, on a container or a compose project. The flags are `View`, `Start`, `Stop`, `Restart`, `Delete`, `Update`, `Logs`, `Execute` and `Edit` (compose files only). Presets: `ReadOnly`, `Standard`, `Full`.

### Rate Limiting

| Policy | Limit | Partition |
|--------|-------|-----------|
| Login | 20 / 15 min | IP |
| Token refresh | 20 / 15 min | IP |
| Forgot password | 5 / hour | IP |
| Reset password | 5 / hour | IP |
| General API | 100 / min | User (IP when anonymous) |

### Security Headers

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 1; mode=block
Referrer-Policy: strict-origin-when-cross-origin
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
  img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' ws: wss:;
  frame-ancestors 'none'; base-uri 'self'; form-action 'self'
Permissions-Policy: geolocation=(), microphone=(), camera=()
Strict-Transport-Security: max-age=31536000; includeSubDomains   (HTTPS only)
```

---

## 5. API Overview

### Endpoint Categories

| Category | Base Path | Access |
|----------|-----------|--------|
| Authentication | `/api/auth` | Public (login, refresh, password reset) / authenticated |
| Profile | `/api/profile` | Authenticated |
| Dashboard | `/api/dashboard` | Authenticated |
| Containers | `/api/containers` | Authenticated + permissions (updates: admin) |
| Compose projects | `/api/compose/projects` | Authenticated + permissions |
| Compose files / editor | `/api/compose/files`, `/api/compose/projects/{name}/files` | Authenticated + permissions (refresh: admin) |
| Compose updates | `/api/compose/.../check-updates`, `update`, `update-all` | Admin |
| Operations | `/api/operations` | Authenticated |
| Images | `/api/images` | Admin |
| Registries | `/api/registry` | Admin |
| Users / Roles | `/api/users`, `/api/roles` | Admin |
| User groups | `/api/usergroups` | Authenticated |
| Permissions | `/api/permissions` | Authenticated (writes: admin) |
| Settings | `/api/config` | Admin |
| Notifications | `/api/notifications/test`, `/api/notifications/test/webhook` | Admin |
| App logs | `/api/app-logs` | Admin |
| System | `/api/system` | Public (`version`, `health`) / admin (self-update) |
| Events | `/api/events/stream` | Authenticated |
| Health | `/health` (public), `/health/detailed` (admin) | |

Dev-only endpoints live under `/api/dev/...` (maintenance simulation, test compose setup).

### Response Format

**Success:**
```json
{
  "data": { ... },
  "success": true,
  "message": "Operation completed"
}
```

**Error:**
```json
{
  "success": false,
  "message": "Error description",
  "errorCode": "ERROR_CODE",
  "errors": { "field": ["validation error"] }
}
```

`ErrorHandlingMiddleware` maps exceptions to this format. Domain code throws `AppException` subclasses (`NotFoundException`, `ForbiddenException`, ...) that carry their HTTP status and error code.

### Error Codes

| Code | Description |
|------|-------------|
| `SERVER_ERROR` | Unexpected server error |
| `VALIDATION_ERROR` | Request validation failed |
| `BAD_REQUEST` | Invalid request |
| `UNAUTHORIZED` | Not authenticated |
| `PERMISSION_DENIED` | Missing permission |
| `CONFLICT` | Conflicting state |
| `RESOURCE_NOT_FOUND` / `PROJECT_NOT_FOUND` / `OPERATION_NOT_FOUND` / `FILE_NOT_FOUND` | Resource missing |
| `SELF_PROJECT_PROTECTED` / `SELF_CONTAINER_PROTECTED` | Operation on Lighthouse itself refused |
| `INVALID_COMPOSE_FILE` | Compose file cannot be parsed |
| `FILE_MODIFIED` | File changed on disk since it was loaded |
| `DOCKER_OPERATION_FAILED` / `OPERATION_FAILED` / `UPDATE_FAILED` | Operation failure |

---

## 6. Development & Testing

### Backend Commands

```bash
cd lighthouse-back

dotnet restore                                   # Restore dependencies
dotnet watch run --project lighthouse-back       # Run with hot-reload
dotnet build -c Release                          # Build for production
dotnet test                                      # Run tests

# Database migrations (applied automatically on startup)
dotnet ef migrations add MigrationName --project lighthouse-back
```

### Frontend Commands

```bash
cd lighthouse-front

npm install      # Install dependencies
npm run dev      # Development server
npm run build    # Production build
npm run check    # Type checking
npm run lint     # ESLint
npm test         # Vitest
```

### Docker Commands

```bash
docker compose up --build   # Build and run
docker compose up -d        # Background
docker compose logs -f      # Follow logs
docker compose down         # Stop
```

### Testing

**Backend (xUnit):** about 580 unit tests over controllers, services, middleware and utilities. They use Moq, plus EF Core InMemory for data access.

**Frontend (Vitest):** unit tests for stores and utilities (`*.test.ts`). There are no E2E tests.

---

## 7. Configuration

### Environment Variables

All settings can be overridden with environment variables in double-underscore notation:

```bash
# Database
Database__ConnectionString="Data Source=/app/data/app.db"

# JWT
Jwt__Secret="your-32-char-secret-key-minimum"
Jwt__ExpirationMinutes=15

# Docker
Docker__Host="unix:///var/run/docker.sock"

# CORS
Cors__Origins__0="http://localhost:5173"

# Compose Discovery
ComposeDiscovery__RootPath="/app/compose-files"
ComposeDiscovery__ScanDepthLimit=5

# Self-update
SelfUpdate__Enabled=true
SelfUpdate__GitHubRepo="JordanOlivet/lighthouse"

# Email (password reset)
Email__Provider="Mock"            # or "Resend"
Email__Resend__ApiKey="..."
Email__AppBaseUrl="http://localhost:3030"

# Logging (initial level only; changeable live in Settings > General)
Serilog__MinimumLevel__Default="Information"
```

Other `appsettings.json` sections: `Security`, `Maintenance` (reconnect/backoff during self-update), `UpdateCheck` (intervals, concurrency, retries, excluded projects/images), `PasswordReset`.

### Runtime Settings (AppSettings table, edited from the UI)

| Area | Keys |
|------|------|
| Compose auto-update | `AutoUpdateComposeEnabled`, `AutoUpdateComposeCron` |
| App auto-update | `AutoUpdateAppEnabled`, `AutoUpdateAppCron` |
| Image auto-prune | `AutoPruneImagesEnabled`, `AutoPruneImagesCron`, `AutoPruneImagesDanglingOnly` |
| Compose | `ComposeGlobalEnvFile` |
| Notifications | `NotificationsDiscord*`, `NotificationsWebhook*` (see §2 Notifications) |
| Logging | `Logging:MinimumLevel` |

### Ports

| Service | Development | Production |
|---------|-------------|------------|
| Backend API | 5050 | 5050 (internal) |
| Frontend Dev | 5173 | N/A |
| Application | N/A | 3030 → 80 (exposed) |

### Logs

Application logs are written as CLEF files to `/app/logs/app-.clef`, rolled daily and kept for 30 days. Admins browse and tail them from the Application Logs page.
