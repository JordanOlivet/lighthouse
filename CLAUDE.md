# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This repository contains a Docker Compose management system deployed as a **unified single-container application**:

- **lighthouse-back**: .NET 10 Web API backend that interfaces with Docker Engine
- **lighthouse-front**: SvelteKit frontend with bits-ui components

The system provides a web-based interface for managing Docker containers and compose files, with features including user authentication, role-based access control, real-time updates via SSE (Server-Sent Events), a compose file editor, and self-update capabilities.

**Current Version**: See `VERSION` file

**Deployment Architecture**: The production deployment uses a single Docker image containing both frontend and backend, managed by Supervisor with Nginx as a reverse proxy. See [DEPLOYMENT.md](DEPLOYMENT.md) for details.

## Development Commands

### Backend (lighthouse-back)

Solution located in `./lighthouse-back/` with the following structure:
- `lighthouse-back/` - Main API project
- `lighthouse-back.Tests/` - Test project (xUnit + Moq + FluentAssertions)

```bash
# All commands run from ./lighthouse-back/ (solution root)

# Restore dependencies
dotnet restore

# Run in development mode with hot-reload
dotnet watch run --project lighthouse-back

# Run without hot-reload
dotnet run --project lighthouse-back

# Build the project
dotnet build -c Release

# Run tests
dotnet test

# Run specific tests
dotnet test --filter "FullyQualifiedName~ServiceName"

# Create database migration (from solution root)
dotnet ef migrations add MigrationName --project lighthouse-back

# Apply database migrations
dotnet ef database update --project lighthouse-back

# Remove last migration (if not applied)
dotnet ef migrations remove --project lighthouse-back
```

Backend runs at `http://localhost:5050` (http launch profile). Swagger is disabled by default; use the `GET /health` endpoint to verify it's up.

### Frontend (lighthouse-front)

Located in `./lighthouse-front/`

```bash
# Install dependencies
npm install

# Run development server (with hot-reload)
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview

# TypeScript type checking
npm run check

# TypeScript watch mode
npm run check:watch
```

Frontend runs at `http://localhost:5173` (Vite default)

### Docker Deployment (Unified Container)

From repository root:

```bash
# Build and start the unified application
docker compose up --build

# Run in background
docker compose up -d

# View logs
docker compose logs -f

# View logs for the unified container (backend + nginx via Supervisor)
docker compose logs -f lighthouse

# Stop service
docker compose down

# Stop and remove volumes (fresh start)
docker compose down -v

# Rebuild the unified image
docker compose build lighthouse

# For development with local builds
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

Application accessible at `http://localhost:3030` when running via Docker Compose.

**Note**: The unified deployment combines both frontend and backend into a single container managed by Supervisor. For architecture details, see [DEPLOYMENT.md](DEPLOYMENT.md).

## Architecture Overview

### Backend Architecture

The backend follows a layered architecture:

```
src/
├── Controllers/     → API endpoints, HTTP request/response handling
├── Services/        → Business logic and Docker operations
├── DTOs/            → Data transfer objects for API
├── Models/          → Domain entities
├── Data/            → Entity Framework Core context
├── Middleware/      → Authentication, authorization, error handling
├── Configuration/   → Options classes for appsettings
├── Validators/      → FluentValidation validators
├── Filters/         → Action filters
├── Extensions/      → Extension methods (incl. ServiceCollectionExtensions DI registration)
├── Exceptions/      → Domain exception hierarchy (AppException + NotFound/Forbidden/...)
├── Constants/       → Shared constants (ErrorCodes)
├── Security/        → Security-related services
└── Utils/           → Utility classes
```

**Composition root:** `Program.cs` is kept thin — service registrations live in
per-subsystem extension methods in `Extensions/ServiceCollectionExtensions.cs`
(`AddApplicationServices()`, `AddComposeServices()`, `AddImageUpdateServices()`, ...).

**Compose controllers:** the compose API is split by concern across
`ComposeController` (project lifecycle), `ComposeFilesController` (discovery /
conflicts / health), and `ComposeUpdatesController` (image-update endpoints), all
sharing the `api/compose` route prefix.

**Error handling:** `ErrorHandlingMiddleware` maps exceptions to `ApiResponse.Fail`.
Domain code can throw `AppException` subclasses (`NotFoundException`,
`ForbiddenException`, ...) which carry their HTTP status + `ErrorCode`.

**Key Services:**

| Service | Description |
|---------|-------------|
| `DockerService` | Interfaces with Docker daemon using Docker.DotNet |
| `ComposeService` | Handles docker-compose operations (up, down, logs) |
| `ComposeFileScannerService` | Scans filesystem to discover compose files |
| `ComposeFileCacheService` | Thread-safe caching of discovered files |
| `ProjectMatchingService` | Matches Docker projects with discovered files |
| `ConflictResolutionService` | Resolves naming conflicts between files |
| `ComposeCommandClassifierService` | Determines which commands need compose files |
| `SelfUpdateService` | Handles application self-update via GitHub releases |
| `InstanceIdentifierService` | Unique instance ID for update detection |
| `SseConnectionManagerService` | Manages SSE connections for real-time updates |
| `ImageDigestService` | Checks for container image updates |
| `AppLogService` | Reads/streams application logs (CLEF files + live Serilog sink) |
| `JwtTokenService` | Generates and validates JWT tokens |
| `UserService` | User CRUD and authentication |
| `PermissionService` | Role-based access control |
| `RegistryCredentialService` | Docker registry authentication |

**Authentication Flow:**

1. User logs in → receives short-lived access token (60 min) + long-lived refresh token (7 days)
2. Access token stored in localStorage, refresh token in Sessions table
3. Access token expires → frontend uses refresh token to get new access token
4. Logout or password change → refresh token deleted from database
5. Proactive refresh: frontend refreshes token 10 min before expiration

**Database (SQLite):**

- Schema: Users, Roles, UserGroups, AppSettings, Sessions, RegistryCredentials
- Migrations managed via Entity Framework Core
- Default location: `Data/app.db` (development) or `/app/data/app.db` (Docker)
- Migrations run automatically on startup

**Docker Integration:**

- Connects via socket (Linux: `unix:///var/run/docker.sock`, Windows: `npipe://./pipe/docker_engine`)
- Uses Docker.DotNet library for API communication
- Real-time events via Docker event stream

### Frontend Architecture

SvelteKit application with Svelte 5 runes:

```
src/
├── routes/              → SvelteKit file-based routing
│   ├── (protected)/     → Authenticated routes
│   │   ├── dashboard/   → Main dashboard
│   │   ├── containers/  → Container management
│   │   ├── compose/     → Compose files & projects
│   │   ├── settings/    → App settings & self-update
│   │   ├── users/       → User management (admin)
│   │   ├── permissions/ → Role management (admin)
│   │   ├── logs-app/    → Application logs viewer (admin)
│   │   ├── logs/        → Container logs viewer
│   │   └── dev/         → Dev test page (dev only)
│   ├── login/           → Login page
│   ├── forgot-password/ → Password reset
│   └── ...
├── lib/
│   ├── components/      → Reusable UI components
│   ├── stores/          → Svelte 5 runes stores
│   ├── api/             → API client functions (Axios)
│   ├── types/           → TypeScript type definitions
│   ├── utils/           → Utility functions
│   └── i18n/            → Internationalization (fr/en)
└── app.html             → HTML template
```

**Key Technologies:**

| Technology | Version | Purpose |
|------------|---------|---------|
| SvelteKit | 2.48+ | Framework |
| Svelte | 5.43+ | UI with runes |
| TanStack Query | 6.x | Data fetching/caching |
| bits-ui | 2.x | UI components |
| Tailwind CSS | 4.x | Styling |
| Monaco Editor | 0.55+ | Code editor for compose files |
| Axios | 1.x | HTTP client |
| Zod | 4.x | Schema validation |
| i18next | 26.x | Internationalization |

**Real-Time Updates (SSE):**

- Server-Sent Events via `/api/sse/events`
- Events: ContainerChanged, ComposeChanged, ComposeFilesChanged, OperationProgress, MaintenanceMode, ProjectUpdatesChecked
- Automatic reconnection with exponential backoff
- Store: `stores/sse.svelte.ts`

**API Communication:**

- Base URL: `https://localhost:5050` (dev) or relative `/api` (production via nginx)
- JWT token in Authorization header: `Bearer {token}`
- TanStack Query handles caching, refetching, and optimistic updates
- Automatic token refresh on 401 responses

### Compose File Discovery

Automatic file discovery system (v0.21.0+):

- Scans a single root directory (`/app/compose-files` by default) recursively
- Discovers all `.yml` and `.yaml` files containing compose services
- No database storage - files discovered on-demand with a stale-while-revalidate cache: once populated, reads never wait on disk (stale snapshot served, background rescan, `ComposeFilesChanged` SSE on change)
- Configuration via `appsettings.json` → `ComposeDiscovery` section

**Configuration Options:**

```json
{
  "ComposeDiscovery": {
    "RootPath": "/app/compose-files",
    "ScanDepthLimit": 5,
    "CacheDurationSeconds": 10,
    "MaxFileSizeKB": 1024,
    "HostPathMapping": "C:\\path\\on\\host"  // For dev on Windows
  }
}
```

**Disabling Files with `x-disabled`:**

```yaml
x-disabled: true
name: my-project
services:
  web:
    image: nginx:latest
```

### Self-Update System

The application can update itself via GitHub releases:

**Backend Components:**
- `SelfUpdateService`: Orchestrates the update process
- `GitHubReleaseService`: Checks for new releases
- `InstanceIdentifierService`: Unique instance ID for restart detection
- `MaintenanceDevController`: Dev endpoints for testing maintenance mode

**Update Flow:**
1. Check for updates via GitHub API
2. Broadcast `MaintenanceMode` SSE event with `preUpdateInstanceId`
3. Pull new Docker image
4. Launch updater container to recreate app
5. Frontend polls `/api/system/health` for new instance
6. When `instanceId` changes and `isReady=true`, redirect to login

**Frontend Components:**
- `MaintenanceOverlay.svelte`: Displays during update
- `stores/update.svelte.ts`: Manages update state

### Security Architecture

**Critical Security Considerations:**

1. **Docker Socket Access**: Backend has root-level access to host via Docker socket
2. **Path Traversal Prevention**: All file paths validated against `RootPath`
3. **Input Validation**: FluentValidation on all endpoints
4. **Rate Limiting**: 5 auth attempts/15min, 100 requests/min/user
5. **Password Security**: BCrypt with cost factor 12
6. **Application Logging**: Serilog events enriched with the authenticated user (LogContext); admins browse/tail them from the Application Logs page

## Development Setup

### Prerequisites

- Docker Desktop (Windows/Mac) or Docker Engine (Linux)
- .NET 10 SDK
- Node.js 24+ and npm
- Git

### Local Development (Without Docker)

1. **Backend**:
   - Per-developer overrides go in the gitignored `appsettings.Development.local.json`
   - Migrations apply automatically on startup (no manual `dotnet ef database update` needed)
   - Run `dotnet watch run --project lighthouse-back --launch-profile http` for hot-reload
   - Verify with `GET http://localhost:5050/health` (Swagger is disabled by default)

2. **Frontend**:
   - Create `.env.development` with `VITE_API_URL=http://localhost:5050` (dev runs over HTTP)
   - Run `npm install` then `npm run dev`
   - Access at `http://localhost:5173`

   **npm version / lockfile:** CI installs with **npm 10** (Node 24). A locally
   installed npm 11 (Node 25+) resolves `package-lock.json` differently and makes
   CI's `npm ci` fail. To keep the lockfile CI-compatible regardless of local npm:
   - `.githooks/pre-commit` **auto-relocks** the lockfile with the pinned npm
     (`npx -y npm@10.9.2`, see the `relock` script) whenever frontend deps change.
   - Arm it with `git config core.hooksPath .githooks` (the `dev-setup` scripts do
     this); regenerate by hand any time with `npm run relock` (from `lighthouse-front/`).
   - Never commit a `package-lock.json` generated by npm 11.

3. **Default Credentials**: Username `admin`, password `adminadmin` (must be changed on first login)

### Dev Test Page

Access `/dev` (development only) for testing features:

**Bulk Update Simulation:**
- Configure fake projects and simulate update progress
- Test BulkUpdateDialog with mock SSE events

**Docker Testing:**
- Create test compose files
- Force outdated images (cross-tag for digest mismatch)
- Restore original images

**Maintenance Mode Simulation:**
- View current instance status (instanceId, isReady, uptime)
- Simulate full maintenance cycle without real update
- Reset instance ID (simulates container restart)
- Toggle ready state

## Testing

### Backend Tests

Located in `lighthouse-back.Tests/`:

```
Controllers/
├── SseControllerTests.cs
├── SystemControllerTests.cs
└── UsersControllerTests.cs

Services/
├── InstanceIdentifierServiceTests.cs
├── SelfUpdateServiceTests.cs
├── ComposeFileScannerTests.cs
├── ComposeFileCacheServiceTests.cs
├── ComposeCommandClassifierTests.cs
├── ConflictResolutionServiceTests.cs
├── ProjectMatchingServiceTests.cs
├── PathValidatorTests.cs
├── OperationServiceTests.cs
├── UserServiceTests.cs
├── SseConnectionManagerTests.cs
├── DockerEventHandlerServiceTests.cs
└── FileServiceTests.cs

Security/
└── ApiKeyProtectorTests.cs
```

Run tests: `dotnet test`

### Frontend Tests

Currently no automated tests configured. Manual testing via dev page.

## API Endpoints

### Public Endpoints
- `GET /api/system/health` - Health check with instance info
- `GET /api/system/version` - Version information
- `POST /api/auth/login` - User login
- `POST /api/auth/refresh` - Refresh token

### Protected Endpoints (require auth)
- `GET /api/containers` - List containers
- `GET /api/compose/projects` - List compose projects
- `GET /api/compose/files` - List discovered compose files
- `POST /api/compose/projects/{name}/{action}` - Execute compose action
- `GET /api/sse/events` - SSE event stream

### Admin Endpoints
- `GET /api/users` - User management
- `GET /api/app-logs/history` - Application logs (paged) — admin
- `GET /api/app-logs/stream` - Application logs live tail (SSE) — admin
- `POST /api/system/update` - Trigger self-update
- `POST /api/compose/refresh` - Force cache refresh

### Dev Endpoints (development only)
- `GET /api/dev/test-compose/status` - Test setup status
- `POST /api/dev/test-compose/setup` - Create test files
- `POST /api/dev/test-compose/force-outdated` - Force image mismatch
- `GET /api/dev/maintenance/status` - Maintenance status
- `POST /api/dev/maintenance/simulate` - Simulate maintenance mode
- `POST /api/dev/maintenance/reset-instance` - Reset instance ID

## Common Issues

1. **Backend cannot connect to Docker daemon**:
   - Linux: `sudo chmod 666 /var/run/docker.sock`
   - Windows: Ensure Docker Desktop is running

2. **CORS errors**: Ensure `Cors:Origins` includes frontend URL

3. **Database locked**: SQLite single-writer. Close DB Browser.

4. **Hot reload not working**: Use `dotnet watch run` and `npm run dev`

5. **SSL certificate errors**: Trust the dev certificate or use HTTP for dev

## Versioning and Releases

Automated semantic versioning based on PR labels. See [RELEASING.md](RELEASING.md).

**Labels:**
- `release-major` - Breaking changes
- `release-minor` - New features
- `release-patch` - Bug fixes

**Version Info:**
```bash
curl https://localhost:5050/api/system/version
# Returns: { version, buildDate, gitCommit, environment }
```

## Configuration Reference

### appsettings.Development.json

```json
{
  "Database": {
    "ConnectionString": "Data Source=../Data/app.db"
  },
  "Jwt": {
    "Secret": "dev-secret-key-at-least-32-chars-long",
    "ExpirationMinutes": 60,
    "RefreshExpirationDays": 7
  },
  "Docker": {
    "Host": "npipe://./pipe/docker_engine"  // Windows
    // "Host": "unix:///var/run/docker.sock"  // Linux
  },
  "Cors": {
    "Origins": ["http://localhost:5173"]
  },
  "ComposeDiscovery": {
    "RootPath": "/app/compose-files",
    "HostPathMapping": "C:\\path\\on\\host"  // Windows dev
  },
  "SelfUpdate": {
    "Enabled": true,
    "Repository": "owner/repo"
  }
}
```

### Environment Variables

Override settings using double underscore notation:
```bash
Database__ConnectionString="Data Source=/path/to/db"
Jwt__Secret="secret-key"
Docker__Host="unix:///var/run/docker.sock"
```
