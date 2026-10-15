import { browser } from '$app/env';
import type { AppUpdateCheckResponse, MaintenanceModeNotification } from '$lib/types/update';
import { updateApi } from '$lib/api/update';
import { logger } from '$lib/utils/logger';

// Configuration
const CHECK_INTERVAL_MS = 60 * 60 * 1000; // 1 hour
const MIN_CHECK_INTERVAL_MS = 5 * 60 * 1000; // 5 minutes minimum between checks

// Internal state for periodic checking
let checkIntervalId: ReturnType<typeof setInterval> | null = null;
let isPeriodicCheckRunning = false;

// Generation counter to prevent stale responses from overwriting cleared state
let stateGeneration = 0;

// Svelte 5 pattern: export state object with properties
export const updateState = $state({
  // Update check results
  updateInfo: null as AppUpdateCheckResponse | null,
  lastChecked: null as Date | null,

  // Loading states
  isCheckingUpdate: false,
  isUpdating: false,

  // Maintenance mode (received via SignalR)
  isInMaintenance: false,
  maintenanceMessage: '' as string,
  gracePeriodSeconds: 0,
  /** Instance ID before the update, used to detect when new instance is ready */
  preUpdateInstanceId: null as string | null,

  // Reconnection state during maintenance
  reconnectAttempt: 0,
  reconnectCountdown: 0,

  // Errors
  checkError: null as string | null,
  updateError: null as string | null
});

// Derived state as getters
export const hasUpdate = {
  get current() { return updateState.updateInfo?.updateAvailable ?? false; }
};

export const currentVersion = {
  get current() { return updateState.updateInfo?.currentVersion ?? 'unknown'; }
};

export const latestVersion = {
  get current() { return updateState.updateInfo?.latestVersion ?? 'unknown'; }
};

export const updateCount = {
  get current() { return updateState.updateInfo?.changelog?.length ?? 0; }
};

export const hasBreakingChanges = {
  get current() { return updateState.updateInfo?.summary?.hasBreakingChanges ?? false; }
};

export const hasSecurityFixes = {
  get current() { return updateState.updateInfo?.summary?.hasSecurityFixes ?? false; }
};

// Actions
export function setUpdateInfo(info: AppUpdateCheckResponse | null) {
  updateState.updateInfo = info;
  updateState.lastChecked = new Date();
  updateState.checkError = null;
}

export function setCheckingUpdate(checking: boolean) {
  updateState.isCheckingUpdate = checking;
}

export function setUpdating(updating: boolean) {
  updateState.isUpdating = updating;
}

export function setCheckError(error: string | null) {
  updateState.checkError = error;
}

export function setUpdateError(error: string | null) {
  updateState.updateError = error;
}

export function enterMaintenanceMode(notification: MaintenanceModeNotification) {
  updateState.isInMaintenance = notification.isActive;
  updateState.maintenanceMessage = notification.message;
  updateState.gracePeriodSeconds = notification.gracePeriodSeconds;
  updateState.preUpdateInstanceId = notification.preUpdateInstanceId ?? null;
  updateState.reconnectAttempt = 0;
  updateState.reconnectCountdown = 0;

  // Stop periodic checking during maintenance to prevent race conditions
  if (notification.isActive) {
    stopPeriodicCheck();
  }
}

export function exitMaintenanceMode() {
  updateState.isInMaintenance = false;
  updateState.maintenanceMessage = '';
  updateState.gracePeriodSeconds = 0;
  updateState.preUpdateInstanceId = null;
  updateState.reconnectAttempt = 0;
  updateState.reconnectCountdown = 0;
  updateState.isUpdating = false;
}

export function updateReconnectState(attempt: number, countdown: number) {
  updateState.reconnectAttempt = attempt;
  updateState.reconnectCountdown = countdown;
}

export function clearUpdateInfo() {
  // Increment generation to invalidate any in-flight requests
  stateGeneration++;
  updateState.updateInfo = null;
  updateState.lastChecked = null;
  updateState.checkError = null;
  updateState.updateError = null;
}

/**
 * Check for updates from the API and update the store.
 * Only runs if user is admin (API will return 403 otherwise).
 * @param force - If true, skip the minimum interval check
 */
export async function checkForUpdates(force = false): Promise<AppUpdateCheckResponse | null> {
  if (!browser) return null;

  // Don't check during maintenance mode
  if (updateState.isInMaintenance) {
    logger.log('[Update Store] In maintenance mode, skipping check');
    return null;
  }

  // Don't check if already checking
  if (updateState.isCheckingUpdate) {
    logger.log('[Update Store] Check already in progress, skipping');
    return updateState.updateInfo;
  }

  // Don't check too frequently (unless forced)
  if (!force && updateState.lastChecked) {
    const timeSinceLastCheck = Date.now() - updateState.lastChecked.getTime();
    if (timeSinceLastCheck < MIN_CHECK_INTERVAL_MS) {
      logger.log('[Update Store] Checked recently, using cached result');
      return updateState.updateInfo;
    }
  }

  // Capture generation to detect if state was cleared during the async operation
  const currentGeneration = stateGeneration;

  updateState.isCheckingUpdate = true;
  updateState.checkError = null;

  try {
    logger.log('[Update Store] Checking for updates...');
    const result = await updateApi.checkAppUpdate();

    // Check if state was cleared while we were fetching (e.g., during maintenance/update)
    if (stateGeneration !== currentGeneration) {
      logger.log('[Update Store] State was cleared during check, discarding result');
      return null;
    }

    updateState.updateInfo = result;
    updateState.lastChecked = new Date();

    if (result.updateAvailable) {
      logger.log('[Update Store] Update available:', result.currentVersion, '->', result.latestVersion);
    } else {
      logger.log('[Update Store] Application is up to date:', result.currentVersion);
    }

    return result;
  } catch (error: unknown) {
    // Check if state was cleared while we were fetching
    if (stateGeneration !== currentGeneration) {
      logger.log('[Update Store] State was cleared during check, ignoring error');
      return null;
    }

    // Don't log 403 errors as they're expected for non-admin users
    if (error && typeof error === 'object' && 'response' in error) {
      const axiosError = error as { response?: { status?: number } };
      if (axiosError.response?.status === 403) {
        logger.log('[Update Store] User is not admin, skipping update check');
        return null;
      }
    }

    const errorMessage = error instanceof Error ? error.message : 'Unknown error';
    logger.error('[Update Store] Failed to check for updates:', errorMessage);
    updateState.checkError = errorMessage;
    return null;
  } finally {
    updateState.isCheckingUpdate = false;
  }
}

/**
 * Start periodic update checking.
 * Should be called once when the app loads (for admin users).
 */
export function startPeriodicCheck(): void {
  if (!browser || isPeriodicCheckRunning) return;

  isPeriodicCheckRunning = true;
  logger.log('[Update Store] Starting periodic update check (interval:', CHECK_INTERVAL_MS / 1000 / 60, 'minutes)');

  // // Do an initial check
  // checkForUpdates();

  // Set up periodic checking
  checkIntervalId = setInterval(() => {
    checkForUpdates();
  }, CHECK_INTERVAL_MS);
}

/**
 * Stop periodic update checking.
 * Should be called when the user logs out.
 */
export function stopPeriodicCheck(): void {
  if (checkIntervalId) {
    clearInterval(checkIntervalId);
    checkIntervalId = null;
  }
  isPeriodicCheckRunning = false;
  logger.log('[Update Store] Stopped periodic update check');
}

/**
 * Check if periodic checking is running.
 */
export function isPeriodicCheckActive(): boolean {
  return isPeriodicCheckRunning;
}
