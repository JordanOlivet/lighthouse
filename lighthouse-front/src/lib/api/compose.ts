import { apiClient } from './client';
import type {
  ApiResponseWrapper,
  ComposeProject,
  ComposeUpRequest,
  ComposeDownRequest,
  ComposeOperationResponse,
  ComposeFileDetails,
  DiscoveredComposeFileDto,
  ConflictsResponse,
  ComposeHealthDto,
  RefreshComposeResponse,
  ProjectFilesResponse,
  ProjectFile,
  UpdateProjectFileRequest
} from '$lib/types';

// Compose Files API

export const composeApi = {
  // ============================================
  // Compose Discovery Endpoints (New)
  // ============================================

  /**
   * Get all discovered compose files from the configured root directory.
   *
   * Returns all compose files found during automatic discovery, including:
   * - File paths and project names
   * - Validity status (YAML parsing)
   * - Disabled status (x-disabled flag)
   * - Service lists extracted from each file
   *
   * @returns Promise resolving to an array of discovered compose files
   * @throws Error if the request fails or the server returns an error
   */
  getComposeFiles: async (): Promise<DiscoveredComposeFileDto[]> => {
    const response = await apiClient.get<ApiResponseWrapper<DiscoveredComposeFileDto[]>>('/compose/files');
    return response.data.data || [];
  },

  /**
   * Get project name conflicts between compose files.
   *
   * Conflicts occur when multiple compose files have the same project name.
   * To resolve conflicts, add 'x-disabled: true' to unwanted files.
   *
   * @returns Promise resolving to conflict information including conflicting files and resolution steps
   * @throws Error if the request fails or the server returns an error
   */
  getComposeConflicts: async (): Promise<ConflictsResponse> => {
    const response = await apiClient.get<ApiResponseWrapper<ConflictsResponse>>('/compose/conflicts');
    if (!response.data.data) {
      throw new Error('Failed to retrieve conflicts');
    }
    return response.data.data;
  },

  /**
   * Get compose discovery and Docker daemon health status.
   *
   * Returns health information for:
   * - Compose discovery subsystem (directory access, degraded mode)
   * - Docker daemon connection (version, connectivity)
   * - Overall system status (healthy, degraded, critical)
   *
   * No authentication required - this is a diagnostic endpoint.
   *
   * @returns Promise resolving to health status information
   * @throws Error if the request fails or the server returns an error
   */
  getComposeHealth: async (): Promise<ComposeHealthDto> => {
    const response = await apiClient.get<ApiResponseWrapper<ComposeHealthDto>>('/compose/health');
    if (!response.data.data) {
      throw new Error('Failed to retrieve health status');
    }
    return response.data.data;
  },

  /**
   * Manually refresh compose file discovery cache (admin only).
   *
   * Invalidates the cache and performs a fresh filesystem scan.
   * Use this after adding, modifying, or removing compose files.
   *
   * Requires admin role.
   *
   * @returns Promise resolving to refresh results with file count and timestamp
   * @throws Error if the request fails, user is not authorized, or the server returns an error
   */
  refreshComposeFiles: async (): Promise<RefreshComposeResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<RefreshComposeResponse>>('/compose/refresh');
    if (!response.data.data) {
      throw new Error('Failed to refresh compose files');
    }
    return response.data.data;
  },

  // ============================================
  // Compose Project File Editing Endpoints
  // ============================================

  /**
   * Get the editable files (compose file + adjacent .env) of a project.
   * Files are resolved server-side from the project name; no path is exposed.
   * Requires View permission on the project.
   */
  getProjectFiles: async (projectName: string): Promise<ProjectFilesResponse> => {
    const response = await apiClient.get<ApiResponseWrapper<ProjectFilesResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/files`
    );
    if (!response.data.data) {
      throw new Error('Failed to load project files');
    }
    return response.data.data;
  },

  /**
   * Update one editable file of a project (optimistic locking via ETag).
   * Requires Edit permission on the project. Returns the file with its new ETag.
   */
  updateProjectFile: async (
    projectName: string,
    request: UpdateProjectFileRequest
  ): Promise<ProjectFile> => {
    const response = await apiClient.put<ApiResponseWrapper<ProjectFile>>(
      `/compose/projects/${encodeURIComponent(projectName)}/files`,
      request
    );
    if (!response.data.data) {
      throw new Error('Failed to update project file');
    }
    return response.data.data;
  },

  // Compose Projects API

  // List all compose projects
  // @param options.refreshState - If true, only invalidates Docker state cache (fast, for container state changes)
  // @param options.refresh - If true, invalidates both caches including file discovery (slow, for file changes)
  listProjects: async (options?: { refreshState?: boolean; refresh?: boolean }): Promise<ComposeProject[]> => {
    const params: Record<string, string> = {};
    if (options?.refreshState) params.refreshState = 'true';
    if (options?.refresh) params.refresh = 'true';
    const response = await apiClient.get<ApiResponseWrapper<ComposeProject[]>>(
      '/compose/projects',
      Object.keys(params).length > 0 ? { params } : undefined
    );
    return response.data.data || [];
  },

  // Start compose project (docker-compose up)
  upProject: async (projectName: string, request?: ComposeUpRequest): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/up`,
      request || {}
    );
    if (!response.data.data) {
      throw new Error('Failed to start project');
    }
    return response.data.data;
  },

  // Stop compose project (docker-compose down)
  downProject: async (projectName: string, request?: ComposeDownRequest): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/down`,
      request || {}
    );
    if (!response.data.data) {
      throw new Error('Failed to stop project');
    }
    return response.data.data;
  },

  // Re-run a one-shot service (compose up -d --force-recreate <service> + its restart: true dependents)
  rerunOneShot: async (projectName: string, serviceName: string): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/services/${encodeURIComponent(serviceName)}/rerun`
    );
    if (!response.data.data) {
      throw new Error('Failed to re-run one-shot service');
    }
    return response.data.data;
  },

  // Restart compose project
  restartProject: async (projectName: string, timeout?: number): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/restart`,
      { timeout }
    );
    if (!response.data.data) {
      throw new Error('Failed to restart project');
    }
    return response.data.data;
  },

  // Stop compose project services
  stopProject: async (projectName: string, timeout?: number): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/stop`,
      { timeout }
    );
    if (!response.data.data) {
      throw new Error('Failed to stop project');
    }
    return response.data.data;
  },

  // Start compose project services
  startProject: async (projectName: string): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/start`,
      {}
    );
    if (!response.data.data) {
      throw new Error('Failed to start project');
    }
    return response.data.data;
  },

  // Pull compose project images
  pullProject: async (projectName: string, ignorePullFailures?: boolean): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/pull`,
      { ignorePullFailures }
    );
    if (!response.data.data) {
      throw new Error('Failed to pull project images');
    }
    return response.data.data;
  },

  // Build compose project
  buildProject: async (
    projectName: string,
    noCache?: boolean,
    pull?: boolean
  ): Promise<ComposeOperationResponse> => {
    const response = await apiClient.post<ApiResponseWrapper<ComposeOperationResponse>>(
      `/compose/projects/${encodeURIComponent(projectName)}/build`,
      { noCache, pull }
    );
    if (!response.data.data) {
      throw new Error('Failed to build project');
    }
    return response.data.data;
  },

// Get compose project details
  getProjectDetails: async (
    projectName: string
  ): Promise<ComposeProject> => {
    const response = await apiClient.get<ApiResponseWrapper<ComposeProject>>(
      `/compose/projects/${encodeURIComponent(projectName)}`
    );
    if (!response.data.data) {
      throw new Error(`Failed to get project ${projectName}`);
    }
    return response.data.data;
  },

  // Get parsed compose file details with structured information
  getProjectParsedDetails: async (
    projectName: string
  ): Promise<ComposeFileDetails> => {
    const response = await apiClient.get<ApiResponseWrapper<ComposeFileDetails>>(
      `/compose/projects/${encodeURIComponent(projectName)}/parsed`
    );
    if (!response.data.data) {
      throw new Error(`Failed to get parsed details for project ${projectName}`);
    }
    return response.data.data;
  }
};
