import { browser } from '$app/env';
import type { CheckAllUpdatesResponse, ProjectUpdateSummary, ProjectUpdatesCheckedEvent } from '$lib/types/update';
import { updateApi } from '$lib/api/update';
import configApi from '$lib/api/config';
import { logger } from '$lib/utils/logger';

// Configuration
const MIN_CHECK_INTERVAL_MS = 5 * 60 * 1000; // 5 minutes minimum between manual checks

// Svelte 5 pattern: export state object with properties
export const projectUpdateState = $state({
  // Update check results
  checkResult: null as CheckAllUpdatesResponse | null,
  lastChecked: null as Date | null,

  // Per-project update status (projectName -> hasUpdates)
  projectsWithUpdates: {} as Record<string, boolean>,

  // Loading states
  isCheckingAll: false,

  // Configuration (default 12h to keep registry load low; user-overridable in Settings)
  checkIntervalMinutes: 720,

  // Errors
  checkError: null as string | null
});

// Derived state as getters
export const hasAnyUpdates = {
  get current() {
    return (projectUpdateState.checkResult?.projectsWithUpdates ?? 0) > 0;
  }
};

export const projectsWithUpdatesCount = {
  get current() {
    return projectUpdateState.checkResult?.projectsWithUpdates ?? 0;
  }
};

export const totalServicesWithUpdates = {
  get current() {
    return projectUpdateState.checkResult?.totalServicesWithUpdates ?? 0;
  }
};

// Check if a specific project has updates
export function projectHasUpdates(projectName: string): boolean {
  return projectUpdateState.projectsWithUpdates[projectName] ?? false;
}

// Get project update summary
export function getProjectSummary(projectName: string): ProjectUpdateSummary | undefined {
  return projectUpdateState.checkResult?.projects.find(p => p.projectName === projectName);
}

// Actions
export function setCheckResult(result: CheckAllUpdatesResponse | null) {
  projectUpdateState.checkResult = result;
  projectUpdateState.lastChecked = new Date();
  projectUpdateState.checkError = null;

  // Update per-project record
  const updates: Record<string, boolean> = {};
  if (result) {
    for (const project of result.projects) {
      updates[project.projectName] = project.servicesWithUpdates > 0;
    }
  }
  projectUpdateState.projectsWithUpdates = updates;
}

export function setCheckingAll(checking: boolean) {
  projectUpdateState.isCheckingAll = checking;
}

export function setCheckError(error: string | null) {
  projectUpdateState.checkError = error;
}

export function clearProjectUpdateState() {
  projectUpdateState.checkResult = null;
  projectUpdateState.lastChecked = null;
  projectUpdateState.checkError = null;
  projectUpdateState.projectsWithUpdates = {};
}

/**
 * Handle SSE ProjectUpdatesChecked event (from backend periodic or manual check).
 * Updates the store with the latest results without triggering a new API call.
 */
export function handleProjectUpdatesCheckedEvent(event: ProjectUpdatesCheckedEvent): void {
  logger.log(`[Project Update Store] Received SSE update (trigger: ${event.trigger}): ${event.projectsWithUpdates} projects with updates`);

  const result: CheckAllUpdatesResponse = {
    projects: event.projects,
    projectsChecked: event.projectsChecked,
    projectsWithUpdates: event.projectsWithUpdates,
    totalServicesWithUpdates: event.totalServicesWithUpdates,
    checkedAt: event.checkedAt
  };

  setCheckResult(result);
}

/**
 * Load cached update status from the backend (for initial page load).
 * Uses the global status endpoint which reads from cache.
 */
export async function loadCachedUpdateStatus(): Promise<void> {
  if (!browser) return;

  try {
    const summaries = await updateApi.getProjectUpdateStatus();
    if (summaries && summaries.length > 0) {
      const updates: Record<string, boolean> = {};
      let totalWithUpdates = 0;
      let totalServices = 0;

      for (const summary of summaries) {
        updates[summary.projectName] = summary.servicesWithUpdates > 0;
        if (summary.servicesWithUpdates > 0) totalWithUpdates++;
        totalServices += summary.servicesWithUpdates;
      }

      projectUpdateState.projectsWithUpdates = updates;
      projectUpdateState.checkResult = {
        projects: summaries,
        projectsChecked: summaries.length,
        projectsWithUpdates: totalWithUpdates,
        totalServicesWithUpdates: totalServices,
        checkedAt: new Date().toISOString()
      };
      projectUpdateState.lastChecked = new Date();

      logger.log(`[Project Update Store] Loaded cached status: ${totalWithUpdates} projects with updates`);
    }
  } catch (error: unknown) {
    // Don't log 403 errors as they're expected for non-admin users
    if (error && typeof error === 'object' && 'response' in error) {
      const axiosError = error as { response?: { status?: number } };
      if (axiosError.response?.status === 403) {
        return;
      }
    }
    logger.error('[Project Update Store] Failed to load cached update status:', error);
  }
}

/**
 * Check for updates across all projects (manual trigger).
 * @param force - If true, skip the minimum interval check
 */
export async function checkAllProjectUpdates(force = false): Promise<CheckAllUpdatesResponse | null> {
  if (!browser) return null;

  // Don't check if already checking
  if (projectUpdateState.isCheckingAll) {
    logger.log('[Project Update Store] Check already in progress, skipping');
    return projectUpdateState.checkResult;
  }

  // Don't check too frequently (unless forced)
  if (!force && projectUpdateState.lastChecked) {
    const timeSinceLastCheck = Date.now() - projectUpdateState.lastChecked.getTime();
    if (timeSinceLastCheck < MIN_CHECK_INTERVAL_MS) {
      logger.log('[Project Update Store] Checked recently, using cached result');
      return projectUpdateState.checkResult;
    }
  }

  projectUpdateState.isCheckingAll = true;
  projectUpdateState.checkError = null;

  try {
    logger.log('[Project Update Store] Checking for updates across all projects...');
    const result = await updateApi.checkAllProjectUpdates();

    // Note: SSE event will also be received, but we set immediately for responsiveness
    setCheckResult(result);

    if (result.projectsWithUpdates > 0) {
      logger.log(
        `[Project Update Store] Found updates: ${result.projectsWithUpdates} projects, ${result.totalServicesWithUpdates} services`
      );
    } else {
      logger.log('[Project Update Store] All projects are up to date');
    }

    return result;
  } catch (error: unknown) {
    // Don't log 403 errors as they're expected for non-admin users
    if (error && typeof error === 'object' && 'response' in error) {
      const axiosError = error as { response?: { status?: number } };
      if (axiosError.response?.status === 403) {
        logger.log('[Project Update Store] User is not admin, skipping update check');
        return null;
      }
    }

    const errorMessage = error instanceof Error ? error.message : 'Unknown error';
    logger.error('[Project Update Store] Failed to check for updates:', errorMessage);
    projectUpdateState.checkError = errorMessage;
    return null;
  } finally {
    projectUpdateState.isCheckingAll = false;
  }
}

/**
 * Save check interval to settings (AppSettings).
 */
export async function saveIntervalToSettings(intervalMinutes: number): Promise<boolean> {
  if (!browser) return false;

  try {
    await configApi.updateSetting('ProjectUpdateCheckIntervalMinutes', { value: intervalMinutes.toString() });
    projectUpdateState.checkIntervalMinutes = intervalMinutes;
    logger.log(`[Project Update Store] Saved check interval: ${intervalMinutes} minutes`);
    return true;
  } catch (error) {
    logger.error('[Project Update Store] Failed to save interval to settings:', error);
    return false;
  }
}

/**
 * Manually mark a project as updated (removes from updates list).
 * Called after successfully updating a project.
 */
export function markProjectAsUpdated(projectName: string): void {
  projectUpdateState.projectsWithUpdates = { ...projectUpdateState.projectsWithUpdates, [projectName]: false };

  // Also update the check result if it exists
  if (projectUpdateState.checkResult) {
    const project = projectUpdateState.checkResult.projects.find(p => p.projectName === projectName);
    if (project) {
      // Create new project with 0 updates
      const updatedProjects = projectUpdateState.checkResult.projects.map(p =>
        p.projectName === projectName
          ? { ...p, servicesWithUpdates: 0 }
          : p
      );

      // Recalculate counts
      const projectsWithUpdates = updatedProjects.filter(p => p.servicesWithUpdates > 0).length;
      const totalServicesWithUpdates = updatedProjects.reduce((sum, p) => sum + p.servicesWithUpdates, 0);

      projectUpdateState.checkResult = {
        ...projectUpdateState.checkResult,
        projects: updatedProjects,
        projectsWithUpdates,
        totalServicesWithUpdates
      };
    }
  }
}
