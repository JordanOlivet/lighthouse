<div align="center">
  <img src="resources/lighthouse-logo.svg" alt="Lighthouse logo" width="128" height="128" />

  <h1>Lighthouse</h1>

  <p>A web-based interface for managing Docker containers and Docker Compose projects.</p>
</div>

## Overview

Lighthouse provides a modern web UI for managing your Docker infrastructure. It allows you to:

- View and control Docker containers
- Manage Docker Compose projects with automatic file discovery
- Edit compose files with syntax highlighting
- Monitor container logs and statistics in real-time
- Manage users with role-based access control
- Manage Docker registry credentials (Docker Hub, GHCR, custom registries)
- Self-update capability with maintenance mode

**Current Version**: See [VERSION](VERSION) file or [Releases](../../releases)

## Quick Start

### Docker Deployment (Recommended)

```bash
# 1. Create a directory for your compose files
mkdir compose-files

# 2. Create docker-compose.yml
cat > docker-compose.yml << 'EOF'
services:
  lighthouse:
    image: ghcr.io/jordanolivet/lighthouse:latest
    container_name: lighthouse
    ports:
      - "3030:80"
    environment:
      # JWT Secret (must be set in .env)
      # WARNING: Generate a strong key in production (min 32 random characters)
      - Jwt__Secret=${JWT_SECRET:-CHANGE-THIS-SECRET-KEY-IN-PRODUCTION-MIN-32-CHARS}
      # Docker Host
      - Docker__Host=${DOCKER_HOST:-unix:///var/run/docker.sock} # For linux host
      # - Docker__Host=${DOCKER_HOST:-npipe://./pipe/docker_engine} # For windows host
      # Logging
      - Serilog__MinimumLevel__Default=${LOG_LEVEL:-Information}
    volumes:
      # Database persistence
      - app-data:/app/data
      # Docker socket access (Linux/Mac)
      - /var/run/docker.sock:/var/run/docker.sock
      # Windows Docker socket (uncomment if on Windows and comment the line above):
      # - //./pipe/docker_engine://./pipe/docker_engine
      # Docker config for registry credentials (optional)
      # IMPORTANT: Use explicit path, not ${HOME} if running with sudo
      - /home/your-user/.docker:/root/.docker
      # Logs persistence
      - ./logs:/app/logs
      # Compose files directory
      - ./compose-files:/app/compose-files
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost/health || exit 1"]
      interval: 10s
      timeout: 10s
      retries: 3
      start_period: 30s

volumes:
  app-data:
    driver: local
EOF

# 3. Start the application
docker compose up -d

# 4. Access at http://localhost:3030
# Default credentials: admin / adminadmin (must be changed on first login)
```

For Windows hosts, replace the Docker socket volume with:
```yaml
- //./pipe/docker_engine://./pipe/docker_engine
```

## Features

### Dashboard
- Container status overview (running, stopped, total)
- System resource usage statistics
- Quick access to all managed containers

### Container Management
- List, start, stop, restart containers
- View container details (ports, volumes, networks)
- Real-time log streaming
- Container statistics (CPU, memory, network)

### Compose Project Management
- **Automatic file discovery** - Drop compose files in `/app/compose-files`
- Execute compose commands (up, down, start, stop, restart)
- View project status and service health
- Edit compose files with Monaco editor (syntax highlighting)
- **Image update detection** - Automatic checking for newer Docker images
- One-click service updates with automatic container recreation
- **One-shot services** - Init / copy / migration tasks are shown as "Completed" or "Failed" instead of "Exited", and can be re-run (see [One-Shot Services](#one-shot-services))

### User Management (Admin)
- Create and manage user accounts
- Role-based access control
- Activity audit logging
- Session management

### Registry Credentials Management (Admin)
- Manage Docker registry authentication from the Settings page
- **Known Registries**: Pre-configured support for Docker Hub and GitHub Container Registry (GHCR)
- **Custom Registries**: Add any private registry (Harbor, GitLab, AWS ECR, etc.)
- **Authentication Types**: Username/Password or Access Token
- Uses Docker's native credential system (`docker login`/`docker logout`)
- Credentials persist on the host via volume mount

### Application Updates (Admin)
- Check for new versions from the Settings page
- One-click update with automatic container recreation
- Maintenance mode with automatic reconnection
- Support for both release versions and development builds

### Compose Project Updates (Admin)
- **Automatic image update detection** - Compares local and remote image digests
- **Configurable check interval** - From 15 minutes to 24 hours (Settings > Project Update)
- **Per-project update status** - See which projects have available updates
- **Selective updates** - Update individual services or all services at once
- **Update policy support** - Control updates per-service via `x-update-policy` in compose files

### Real-Time Updates
- Live container status via Server-Sent Events (SSE)
- Real-time log streaming
- Instant notifications for operations

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `Jwt__Secret` | JWT signing key (min 32 chars) | **Required** |
| `Docker__Host` | Docker daemon connection | `unix:///var/run/docker.sock` |
| `Serilog__MinimumLevel__Default` | Initial log level (see note below) | `Information` |
| `ComposeDiscovery__RootPath` | Compose files directory | `/app/compose-files` |
| `ComposeDiscovery__ScanDepthLimit` | Max directory depth | `5` |
| `ComposeDiscovery__CacheDurationSeconds` | File cache TTL | `10` |

> **Log level:** `Serilog__MinimumLevel__Default` only sets the level at first
> startup. After that, the level is configurable at runtime from the UI
> (**Settings → General**), takes effect immediately (no restart), and is
> persisted in the database — the stored value overrides this variable on later
> startups.

### JWT Secret (Required)

The `Jwt__Secret` environment variable is **mandatory** for the application to start.

**What is it?**
A secret key used to cryptographically sign authentication tokens (JWT). When users log in, the server generates a token signed with this secret. On each subsequent request, the server verifies the token's signature to ensure it hasn't been tampered with.

**Why is it required?**
Without a JWT secret, the application cannot securely authenticate users. The secret ensures that only the server can generate valid tokens - anyone without the secret cannot forge authentication tokens.

**How to generate one:**

```bash
# Linux/macOS
openssl rand -base64 32

# PowerShell (Windows)
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])

# Or use any random string generator (minimum 32 characters)
```

**Important:**
- Must be at least 32 characters long
- Keep it secret - never commit to version control
- Use a different secret for each environment (dev, staging, prod)
- If you change the secret, all existing user sessions will be invalidated

### Volumes

| Path | Description |
|------|-------------|
| `/app/data` | SQLite database (persist this!) |
| `/var/run/docker.sock` | Docker socket (required) |
| `/app/compose-files` | Your compose files to manage |
| `/app/logs` | Application logs — structured JSON, browsable in-app (optional) |
| `/root/.docker` | Docker config for registry credentials (optional) |

### Compose Project Update Checking

The application can automatically check if newer versions of Docker images are available for your compose projects.

**Configuration:**
- Go to **Settings > Project Update** to configure the check interval (15 min to 24 hours)
- Updates are detected by comparing local image digests with remote registry digests

**Update Policy:**

You can control update behavior per-service using the `x-update-policy` extension in your compose files:

```yaml
services:
  web:
    image: nginx:latest
    x-update-policy: auto  # or "disabled" to skip this service

  database:
    image: postgres:15
    x-update-policy: disabled  # Never check for updates
```

**Available policies:**
- `auto` (default) - Check for updates normally
- `disabled` - Skip this service during update checks

**How updates work:**
1. The application periodically checks each service's image digest against the remote registry
2. Projects with available updates are highlighted in the dashboard
3. Click on a project to see which services have updates
4. Update individual services or all services with one click
5. Updates are performed via `docker compose pull` + `docker compose up -d --force-recreate`

### One-Shot Services

Some services are not meant to run continuously: they run a task (copy files, run a
migration, initialize data), then exit with code `0`. Other services wait for them with
`condition: service_completed_successfully`. Lighthouse recognizes these **one-shot**
services and displays them according to their role instead of as stopped containers.

**Detection:**

1. **Explicit label (takes priority)**: `lighthouse.oneshot: "true"` marks the service as a
   one-shot. `lighthouse.oneshot: "false"` disables the automatic detection for it.
2. **Automatic**: another service of the same project depends on it with
   `condition: service_completed_successfully`, and its `restart` policy is `"no"` (or unset).

```yaml
services:
  agent-src:                   # detected automatically
    image: example/agent:latest
    restart: "no"
    command: ["sh", "-c", "cp -r /app/. /dst/"]
    volumes:
      - ./agent-src:/dst

  migrate:                     # no dependent: mark it explicitly
    image: example/migrations:latest
    labels:
      lighthouse.oneshot: "true"

  webui:
    image: example/agent:latest
    depends_on:
      agent-src:
        condition: service_completed_successfully
        restart: true          # restarted when agent-src is re-run
```

**Behavior:**

| Container state | Shown as | Counted as stopped / degrades the project? |
|-----------------|----------|--------------------------------------------|
| exited with code `0` | **Completed** (green), with the time of the last run | No |
| exited with another code | **Failed** (red), with a link to the logs | Yes |
| running | **Running** | No (counts as running) |
| created, never run | **Never run** | No |

- **Re-run**: one-shots have no "Start" button. They have a **Re-run** button instead, which runs
  `docker compose -f <file> up -d --force-recreate <service> <dependents>`. Running services that
  depend on the one-shot with `restart: true` are part of the same `up`, so Compose restarts them
  once the one-shot has completed. (A targeted `up` of the one-shot alone does not restart them.)
- **Updates**: when a one-shot's image changes, the update re-runs it the same way (it is never
  brought back with a plain `docker start`, and is not left recreated-but-stopped). If the whole
  project is stopped, the one-shot is recreated with the new image but not run.
- A one-shot that shares its image with another updated service of the project is not counted as
  a separate update.

### Docker Registry Credentials

To persist Docker registry credentials across container restarts, mount your host's Docker config directory:

```yaml
volumes:
  - /home/your-user/.docker:/root/.docker
```

**Important Notes:**
- Replace `/home/your-user` with your actual home directory path
- **Do NOT use `${HOME}`** in docker-compose.yml if you run `docker compose` with `sudo`, as it will expand to `/root` instead of your user's home directory
- After configuring this volume, you can manage registry credentials from **Settings > Registry Management**
- The application uses Docker's native `docker login` command, so credentials are stored in the standard Docker config format

## Development Setup

### Prerequisites
- Docker Desktop or Docker Engine
- .NET 10 SDK
- Node.js 24+

> **Fastest path:** run `dev-setup/setup.ps1` (Windows) or `dev-setup/setup.sh`
> (Linux/macOS) to install prerequisites and configure everything, then `run.ps1`
> / `run.sh` to build and launch both apps. See [dev-setup/README.md](dev-setup/README.md)
> and [CONTRIBUTING.md](CONTRIBUTING.md).

### Backend

```bash
cd lighthouse-back

# Restore and run (HTTP dev profile on http://localhost:5050)
dotnet restore
dotnet watch run --project lighthouse-back --launch-profile http

# Health check: http://localhost:5050/health
```

### Frontend

```bash
cd lighthouse-front

# Install and run
npm install
npm run dev

# Access at http://localhost:5173
```

### Docker (Full Stack)

```bash
# Build and run both services
docker compose up --build

# Access at http://localhost:3030
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Docker Container                         │
│  ┌─────────────────────────────────────────────────────────┐│
│  │                    Nginx (:80)                          ││
│  │         Reverse Proxy + Static File Server              ││
│  └────────────────────────┬────────────────────────────────┘│
│                           │                                 │
│           ┌───────────────┴───────────────┐                 │
│           │                               │                 │
│           ▼                               ▼                 │
│  ┌─────────────────┐            ┌─────────────────┐         │
│  │   Frontend      │            │    Backend      │         │
│  │   (SvelteKit)   │  /api/*    │   (.NET 10)     │         │
│  │   Static Files  │ ─────────► │   :5050         │         │
│  └─────────────────┘            └────────┬────────┘         │
│                                          │                  │
│                                          ▼                  │
│                                 ┌─────────────────┐         │
│                                 │  SQLite + Docker│         │
│                                 │     Socket      │         │
│                                 └─────────────────┘         │
└─────────────────────────────────────────────────────────────┘
```

### Technology Stack

**Backend (.NET 10)**
- ASP.NET Core Web API
- Entity Framework Core + SQLite
- Docker.DotNet for Docker API
- Server-Sent Events (SSE) for real-time updates
- Serilog for logging

**Frontend (SvelteKit 2)**
- Svelte 5 with runes
- TanStack Svelte Query
- bits-ui components
- Tailwind CSS 4
- Monaco Editor

## Security Notes

### Docker Socket Access

This application requires access to the Docker socket, which grants **root-level access** to the host system. Only deploy in trusted environments.

Recommendations:
- Use HTTPS with a reverse proxy (Traefik, Nginx)
- Change default credentials immediately
- Use strong JWT secrets (generate with `openssl rand -base64 32`)
- Consider Docker API over TLS for remote deployments

### Registry Credentials

When using the registry management feature:
- Credentials are stored using Docker's native credential system
- Passwords are passed securely via stdin (never logged or stored in command history)
- Only admin users can manage registry credentials
- All login/logout operations are recorded in the audit log

### Default Credentials

```
Username: admin
Password: adminadmin
```

You will be required to change the password on first login.

## Documentation

- [CONTRIBUTING.md](CONTRIBUTING.md) - Contributor & dev workflow guide
- [dev-setup/README.md](dev-setup/README.md) - One-shot dev environment bootstrap
- [DEPLOYMENT.md](DEPLOYMENT.md) - Production deployment guide
- [SPECS.md](SPECS.md) - Technical specifications
- [CHANGELOG.md](CHANGELOG.md) - Version history

## License

GPL-2.0 License - see [LICENSE](LICENSE) for details.
