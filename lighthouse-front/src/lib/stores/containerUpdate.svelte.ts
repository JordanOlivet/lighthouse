import { browser } from '$app/env';
import { updateApi } from '$lib/api/update';
import type { ContainerUpdatesCheckedEvent } from '$lib/types/update';
import { logger } from '$lib/utils/logger';

// Svelte 5 state for container updates
export const containerUpdateState = $state({
  // containerId -> hasUpdate
  containersWithUpdates: {} as Record<string, boolean>,
  isCheckingAll: false,
  checkError: null as string | null,
  lastChecked: null as Date | null
});

export const hasAnyContainerUpdates = {
  get current() {
    return Object.values(containerUpdateState.containersWithUpdates).some(v => v);
  }
};

export const containersWithUpdatesCount = {
  get current() {
    return Object.values(containerUpdateState.containersWithUpdates).filter(v => v).length;
  }
};

export function containerHasUpdate(containerId: string): boolean {
  return containerUpdateState.containersWithUpdates[containerId] ?? false;
}

export function setContainerUpdateResult(containerId: string, hasUpdate: boolean): void {
  containerUpdateState.containersWithUpdates = {
    ...containerUpdateState.containersWithUpdates,
    [containerId]: hasUpdate
  };
}

export function markContainerAsUpdated(containerId: string): void {
  containerUpdateState.containersWithUpdates = {
    ...containerUpdateState.containersWithUpdates,
    [containerId]: false
  };
}

export function handleContainerUpdatesCheckedEvent(event: ContainerUpdatesCheckedEvent): void {
  logger.log(`[Container Update Store] Received SSE update: ${event.containersWithUpdates} containers with updates`);

  const updates: Record<string, boolean> = {};
  for (const container of event.containers) {
    updates[container.containerId] = container.updateAvailable;
  }

  containerUpdateState.containersWithUpdates = updates;
  containerUpdateState.lastChecked = new Date();
  containerUpdateState.checkError = null;
}

export async function loadCachedContainerUpdateStatus(): Promise<void> {
  if (!browser) return;

  try {
    const summaries = await updateApi.getContainerUpdateStatus();
    if (summaries && summaries.length > 0) {
      const updates: Record<string, boolean> = {};
      for (const summary of summaries) {
        updates[summary.containerId] = summary.updateAvailable;
      }
      containerUpdateState.containersWithUpdates = updates;
      containerUpdateState.lastChecked = new Date();
      logger.log(`[Container Update Store] Loaded cached status: ${summaries.length} containers`);
    }
  } catch (error) {
    logger.error('[Container Update Store] Failed to load cached container update status:', error);
  }
}

/**
 * Removes stale entries from the update state for containers that no longer exist.
 * Should be called whenever the container list is refreshed.
 */
export function reconcileContainerUpdateState(currentContainerIds: Set<string>): void {
  const current = containerUpdateState.containersWithUpdates;
  const staleIds = Object.keys(current).filter(id => !currentContainerIds.has(id));

  if (staleIds.length === 0) return;

  const cleaned = { ...current };
  for (const id of staleIds) {
    delete cleaned[id];
  }
  containerUpdateState.containersWithUpdates = cleaned;
  logger.log(`[Container Update Store] Reconciled: removed ${staleIds.length} stale container(s)`);
}

export function clearContainerUpdateState(): void {
  containerUpdateState.containersWithUpdates = {};
  containerUpdateState.isCheckingAll = false;
  containerUpdateState.checkError = null;
  containerUpdateState.lastChecked = null;
}
