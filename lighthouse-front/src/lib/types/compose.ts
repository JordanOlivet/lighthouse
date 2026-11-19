import type { EntityState } from "./global";

/**
 * Discovered compose file information from the discovery system.
 * Used by the compose file discovery API.
 */
export interface DiscoveredComposeFileDto {
  /** Full absolute path to the compose file */
  filePath: string;
  /** Project name (from 'name:' attribute or directory name) */
  projectName: string;
  /** Directory containing the compose file */
  directoryPath: string;
  /** Last modification timestamp */
  lastModified: string;
  /** Whether the file is valid YAML with required structure */
  isValid: boolean;
  /** Whether the file is marked with x-disabled: true */
  isDisabled: boolean;
  /** List of service names in the compose file */
  services: string[];
}

// Compose Project Types
export interface ComposeProject {
  name: string;
  path?: string;
  state: EntityState;
  services: ComposeService[];
  composeFiles: string[];
  lastUpdated: Date;
  // New fields from compose discovery revamp
  /** Path to the primary compose file associated with this project (null if not found) */
  composeFilePath?: string | null;
  /** Indicates whether a compose file was found for this project */
  hasComposeFile?: boolean;
  /** Warning message if project has issues (e.g., "No compose file found for this project") */
  warning?: string | null;
  /** Dictionary of available actions and whether they can be performed */
  availableActions?: Record<string, boolean> | null;
  /** Number of services with available updates (from cache, null if not checked) */
  servicesWithUpdates?: number | null;
  /** When the last update check was performed (from cache, null if not checked) */
  lastUpdateCheck?: string | null;
  /** Whether any service in this project is crash-looping */
  isCrashLooping?: boolean;
  /** Whether automatic updates are enabled (false if compose has x-auto-update: false) */
  autoUpdateEnabled?: boolean;
  /** Whether the current user may edit this project's compose file (detail endpoint only) */
  canEdit?: boolean;
}

// ============================================
// Project File Editing Types
// ============================================

/** Kind of editable project file. */
export type ProjectFileKind = 'compose' | 'env';

/** An editable file of a compose project (compose file or adjacent .env). */
export interface ProjectFile {
  kind: ProjectFileKind;
  /** File name only (no path). */
  fileName: string;
  /** Raw file content, null when the file does not exist. */
  content: string | null;
  /** SHA-256 ETag for optimistic locking, null when the file does not exist. */
  etag: string | null;
  /** Whether the file currently exists on disk. */
  exists: boolean;
}

/** All editable files of a compose project. */
export interface ProjectFilesResponse {
  projectName: string;
  files: ProjectFile[];
}

/** Request to update (or create, for .env) a project file. */
export interface UpdateProjectFileRequest {
  kind: ProjectFileKind;
  content: string;
  /** ETag from the last read; required when the file exists. */
  etag: string | null;
}

export interface ComposeService {
  id: string;
  name: string;
  image?: string;
  state: EntityState;
  status?: string;
  ports?: string[];
  health?: string;
  ipAddress?: string;
  isCrashLooping?: boolean;
  /** Compose service name (`name` is the container name). */
  serviceName?: string;
  /** One-shot service (init / copy / migration task expected to exit). */
  isOneShot?: boolean;
  oneShotStatus?: OneShotStatus | null;
  exitCode?: number | null;
  /** ISO date of the end of the last run (one-shots only). */
  finishedAt?: string | null;
}

/** Run status of a one-shot service, as computed by the backend. */
export type OneShotStatus = 'Pending' | 'Running' | 'Completed' | 'Failed';

export interface ComposeProjectDetailsDto{
    name: string,
    path: string,
    isRunning: boolean,
    totalServices: number,
    runningServices: number,
    stoppedServices: number,
    ComposeServiceStatusDto: ComposeService[]
}


// Compose Operation Request Types
export interface ComposeUpRequest {
  detach?: boolean;
  build?: boolean;
  forceRecreate?: boolean;
  noRecreate?: boolean;
  noBuild?: boolean;
  removeOrphans?: boolean;
  timeout?: number;
}

export interface ComposeDownRequest {
  removeVolumes?: boolean;
  removeImages?: 'all' | 'local' | null;
  removeOrphans?: boolean;
  timeout?: number;
}

export interface ComposeRestartRequest {
  timeout?: number;
  noDeps?: boolean;
}

export interface ComposeStopRequest {
  timeout?: number;
}

export interface ComposeStartRequest {
  noDeps?: boolean;
}

export interface ComposePullRequest {
  ignorePullFailures?: boolean;
  quiet?: boolean;
}

export interface ComposeBuildRequest {
  noCache?: boolean;
  pull?: boolean;
  parallel?: boolean;
}

// Compose Operation Response Types
export interface ComposeOperationResponse {
  operationId: string;
  status: string;
  message: string;
}

// Compose File Parsed Details Types
export interface ComposeFileDetails {
  projectName: string;
  version?: string;
  services: Record<string, ServiceDetails>;
  networks?: Record<string, NetworkDetails>;
  volumes?: Record<string, VolumeDetails>;
}

export interface ServiceDetails {
  name: string;
  image?: string;
  build?: string;
  ports?: string[];
  environment?: Record<string, string>;
  labels?: Record<string, string>;
  volumes?: string[];
  dependsOn?: string[];
  restart?: string;
  networks?: Record<string, string>;
}

export interface NetworkDetails {
  name: string;
  driver?: string;
  external?: boolean;
  driverOpts?: Record<string, string>;
  labels?: Record<string, string>;
}

export interface VolumeDetails {
  name: string;
  driver?: string;
  external?: boolean;
  driverOpts?: Record<string, string>;
  labels?: Record<string, string>;
}

// ============================================
// Conflict Detection Types
// ============================================

/**
 * Error information about conflicting compose files with the same project name.
 */
export interface ConflictErrorDto {
  /** The project name that has conflicts */
  projectName: string;
  /** List of file paths that conflict (all have same project name, none are disabled) */
  conflictingFiles: string[];
  /** User-friendly error message */
  message: string;
  /** Step-by-step instructions to resolve the conflict */
  resolutionSteps: string[];
}

/**
 * Response wrapper for the conflicts endpoint
 */
export interface ConflictsResponse {
  /** List of all detected conflicts */
  conflicts: ConflictErrorDto[];
  /** Whether any conflicts exist */
  hasConflicts: boolean;
}

// ============================================
// Health Check Types
// ============================================

/**
 * Status information for the compose file discovery subsystem
 */
export interface ComposeHealthStatusDto {
  /** Status: "healthy" or "degraded" */
  status: 'healthy' | 'degraded';
  /** Configured root path for compose files */
  rootPath: string;
  /** Whether the root path exists on filesystem */
  exists: boolean;
  /** Whether the root path is accessible (readable) */
  accessible: boolean;
  /** Whether the system is running in degraded mode (compose discovery disabled) */
  degradedMode: boolean;
  /** Optional message explaining degraded status */
  message?: string;
  /** Description of the impact when in degraded mode */
  impact?: string;
}

/**
 * Status information for the Docker daemon connection
 */
export interface DockerDaemonStatusDto {
  /** Status: "healthy" or "unhealthy" */
  status: 'healthy' | 'unhealthy';
  /** Whether connected to Docker daemon */
  connected: boolean;
  /** Docker version (if connected) */
  version?: string;
  /** Docker API version (if connected) */
  apiVersion?: string;
  /** Error message if connection failed */
  error?: string;
}

/**
 * Health status information for the compose discovery system.
 * Used by the GET /api/compose/health endpoint.
 */
export interface ComposeHealthDto {
  /** Overall system status: "healthy", "degraded", or "critical" */
  status: 'healthy' | 'degraded' | 'critical';
  /** Compose discovery subsystem status */
  composeDiscovery: ComposeHealthStatusDto;
  /** Docker daemon connection status */
  dockerDaemon: DockerDaemonStatusDto;
}

/**
 * Response from the refresh endpoint
 */
export interface RefreshComposeResponse {
  /** Number of files discovered */
  filesDiscovered: number;
  /** Timestamp of the refresh operation */
  timestamp: string;
  /** Success status */
  success: boolean;
  /** Optional message */
  message?: string;
}
