import type { QueryClient } from '@tanstack/svelte-query';
import {
  onContainerStateChanged,
  onComposeProjectStateChanged,
  onImagesChanged,
  onComposeFilesChanged,
  onOperationUpdate,
  onReconnected,
  type ContainerStateChangedEvent,
  type ComposeProjectStateChangedEvent,
  type ImagesChangedEvent,
} from '$lib/stores/sse.svelte';
import type { OperationUpdateEvent } from '$lib/types';
import { composeApi } from '$lib/api/compose';
import { logger } from '$lib/utils/logger';
import { setCrashLooping, clearCrashLooping, clearAllCrashLooping } from '$lib/stores/crashLoop.svelte';
import { isBatchOperationActive, isProjectUpdating } from '$lib/stores/batchOperation.svelte';

// Configuration
const DEBOUNCE_DELAY_MS = 50; // Minimal debounce to batch truly simultaneous events
const DEDUPE_WINDOW_MS = 1000;

// Crash loop throttle (detection is now backend-side)
const CRASH_LOOP_THROTTLE_MS = 30_000; // 1 refresh max per 30s during crash loop

// Simplified throttle tracker (crash loop detection moved to backend)
interface ThrottleTracker {
  lastRefreshAt: number;
}
const throttleTrackers = new Map<string, ThrottleTracker>();

/**
 * Returns true if the refetch should be throttled for a crash-looping entity.
 */
function shouldThrottleRefetch(entityKey: string, isCrashLooping: boolean): boolean {
  if (!isCrashLooping) {
    throttleTrackers.delete(entityKey);
    return false;
  }

  const tracker = throttleTrackers.get(entityKey);
  const now = Date.now();

  if (!tracker) {
    throttleTrackers.set(entityKey, { lastRefreshAt: now });
    return false; // Allow the first refetch
  }

  if (now - tracker.lastRefreshAt >= CRASH_LOOP_THROTTLE_MS) {
    tracker.lastRefreshAt = now;
    return false;
  }

  return true; // Throttle
}

// Events that indicate actual state changes (not just signals)
const STATE_CHANGING_EVENTS = new Set([
  'start', 'die', 'create', 'destroy', 'pause', 'unpause', 'restart', 'recreate', 'pull'
]);

// Track recent compose events to avoid double invalidation
const recentComposeEvents = new Map<string, number>();

/**
 * Refetches queries by key, bypassing backend cache for compose projects
 */
async function refetchQueries(queryClient: QueryClient, queryKeys: string[][]): Promise<void> {
  const queryCache = queryClient.getQueryCache();

  for (const queryKey of queryKeys) {
    try {
      const matchingQueries = queryCache.findAll({ queryKey });
      if (matchingQueries.length === 0) continue;

      for (const query of matchingQueries) {
        let freshData;
        const keyStr = JSON.stringify(query.queryKey);

        if (keyStr === '["compose","projects"]') {
          // For compose projects, use refreshState=true to only invalidate Docker cache (fast)
          freshData = await composeApi.listProjects({ refreshState: true });
        } else {
          // For other queries, use the queryFn
          const queryFn = query.options.queryFn;
          if (!queryFn) continue;
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          freshData = await (queryFn as any)({ queryKey: query.queryKey });
        }

        queryClient.setQueryData(query.queryKey, freshData);
        logger.log(`[Bridge] Refreshed ${keyStr}`);
      }
    } catch (error) {
      logger.error(`[Bridge] Failed to refetch ${JSON.stringify(queryKey)}:`, error);
    }
  }
}

/**
 * Creates a "leading edge" debounced refetcher:
 * - First event: executes IMMEDIATELY (no delay)
 * - Subsequent events within window: batched and executed after delay
 */
function createDebouncedRefetcher(
  queryClient: QueryClient,
  delayMs: number
): (queryKeys: string[][]) => void {
  const pendingKeys: Set<string> = new Set();
  let timeout: ReturnType<typeof setTimeout> | null = null;
  let isExecuting = false;

  return (queryKeys: string[][]) => {
    // Add all keys to pending set
    queryKeys.forEach(key => pendingKeys.add(JSON.stringify(key)));

    // If already executing or scheduled, let the existing flow handle it
    if (timeout || isExecuting) {
      return;
    }

    // Execute immediately (leading edge)
    const executeNow = async () => {
      isExecuting = true;
      const keysToRefetch = Array.from(pendingKeys).map(k => JSON.parse(k));
      pendingKeys.clear();

      await refetchQueries(queryClient, keysToRefetch);

      // After execution, wait a bit before allowing next immediate execution
      timeout = setTimeout(() => {
        timeout = null;
        isExecuting = false;

        // If more events accumulated during execution, process them
        if (pendingKeys.size > 0) {
          executeNow();
        }
      }, delayMs);
    };

    executeNow();
  };
}

/**
 * Check if an event was recently handled via compose events
 */
function wasRecentlyHandledViaCompose(containerId: string, action: string): boolean {
  const key = `${containerId}-${action}`;
  const timestamp = recentComposeEvents.get(key);

  if (!timestamp) return false;

  const now = Date.now();
  if (now - timestamp < DEDUPE_WINDOW_MS) {
    return true;
  }

  // Clean up old entry
  recentComposeEvents.delete(key);
  return false;
}

/**
 * Mark an event as handled via compose
 */
function markHandledViaCompose(containerId: string, action: string): void {
  const key = `${containerId}-${action}`;
  recentComposeEvents.set(key, Date.now());

  // Cleanup old entries periodically
  if (recentComposeEvents.size > 100) {
    const now = Date.now();
    for (const [k, v] of recentComposeEvents.entries()) {
      if (now - v > DEDUPE_WINDOW_MS * 2) {
        recentComposeEvents.delete(k);
      }
    }
  }
}

/**
 * Sets up the bridge between SSE events and TanStack Query cache invalidation.
 * This should be called once from the protected layout after SSE is connected.
 *
 * @param queryClient - The TanStack Query client instance
 * @returns Cleanup function to remove all subscriptions
 */
export function setupSSEQueryBridge(queryClient: QueryClient): () => void {
  logger.log('[Bridge] Setting up SSE-Query bridge');
  logger.log('[Bridge] QueryClient instance:', queryClient);

  const scheduleRefetch = createDebouncedRefetcher(queryClient, DEBOUNCE_DELAY_MS);
  const unsubscribers: (() => void)[] = [];

  // Handle ComposeProjectStateChanged events
  const unsubComposeProject = onComposeProjectStateChanged((event: ComposeProjectStateChangedEvent) => {
    const action = event.action.toLowerCase();

    if (!STATE_CHANGING_EVENTS.has(action)) {
      logger.log(`[Bridge] Ignoring compose event (not state-changing): ${event.projectName} - ${event.action}`);
      return;
    }

    // Skip if a batch operation is in progress (e.g., during updates)
    // This prevents excessive refreshes during docker compose pull/up operations
    if (isBatchOperationActive() && (action !== 'recreate' && action !== 'pull')) {
      logger.log(`[Bridge] Skipping compose event (batch operation active): ${event.projectName} - ${event.action}`);
      return;
    }

    // Also skip if this specific project is being updated
    if (isProjectUpdating(event.projectName) && (action !== 'recreate' && action !== 'pull')) {
      logger.log(`[Bridge] Skipping compose event (project updating): ${event.projectName} - ${event.action}`);
      return;
    }

    logger.log(`[Bridge] Compose event (state-changing): ${event.projectName} - ${event.action}`);

    markHandledViaCompose(event.containerId, event.action);

    // Update crash loop store from SSE event
    const projectKey = `project:${event.projectName}`;
    const containerKey = `container:${event.containerId}`;
    if (event.isCrashLooping) {
      setCrashLooping(projectKey);
      setCrashLooping(containerKey);
    } else {
      clearCrashLooping(projectKey);
      clearCrashLooping(containerKey);
    }

    if (shouldThrottleRefetch(projectKey, !!event.isCrashLooping)) return;

    scheduleRefetch([
      ['compose', 'projects'],
      ['compose', 'project', event.projectName],
    ]);
  });
  unsubscribers.push(unsubComposeProject);

  // Handle ContainerStateChanged events
  const unsubContainer = onContainerStateChanged((event: ContainerStateChangedEvent) => {
    const action = event.action.toLowerCase();

    if (!STATE_CHANGING_EVENTS.has(action)) {
      logger.log(`[Bridge] Ignoring container event (not state-changing): ${event.containerName} - ${event.action}`);
      return;
    }

    // Skip if a batch operation is in progress (e.g., during updates)
    // This prevents excessive refreshes during docker compose pull/up operations
    if (isBatchOperationActive()) {
      logger.log(`[Bridge] Skipping container event (batch operation active): ${event.containerName} - ${event.action}`);
      return;
    }

    // Check if this was already handled via compose event
    if (wasRecentlyHandledViaCompose(event.containerId, event.action)) {
      logger.log(`[Bridge] Skipping container event (already handled via compose): ${event.containerName} - ${event.action}`);
      return;
    }

    // Update crash loop store from SSE event
    const containerKey = `container:${event.containerId}`;
    if (event.isCrashLooping) {
      setCrashLooping(containerKey);
    } else {
      clearCrashLooping(containerKey);
    }

    if (shouldThrottleRefetch(containerKey, !!event.isCrashLooping)) return;

    logger.log(`[Bridge] Container event (state-changing): ${event.containerName} - ${event.action}`);

    scheduleRefetch([
      ['containers'],
      ['container', event.containerId],
      ['compose', 'projects'],
    ]);
  });
  unsubscribers.push(unsubContainer);

  // Handle ImagesChanged events (emitted after image delete/prune)
  const unsubImages = onImagesChanged((event: ImagesChangedEvent) => {
    // During a bulk delete the dialog suppresses refreshes and invalidates once
    // at the end, so skip the per-item churn.
    if (isBatchOperationActive()) {
      logger.log(`[Bridge] Skipping images event (batch operation active): ${event.action}`);
      return;
    }

    logger.log(`[Bridge] Images event: ${event.action}`);
    scheduleRefetch([['images']]);
  });
  unsubscribers.push(unsubImages);

  // Handle ComposeFilesChanged events (backend served a stale file list, then found changes)
  const unsubComposeFiles = onComposeFilesChanged(() => {
    logger.log('[Bridge] Compose files changed on disk');
    scheduleRefetch([['compose', 'projects'], ['dashboard']]);
  });
  unsubscribers.push(unsubComposeFiles);

  // Handle OperationUpdate events
  const unsubOperation = onOperationUpdate((event: OperationUpdateEvent) => {
    if (event.status !== 'completed' && event.status !== 'failed') {
      return;
    }

    logger.log(`[Bridge] Operation ${event.status}: ${event.type}`);

    const typeLower = (event.type || '').toLowerCase();
    const queriesToInvalidate: string[][] = [];

    if (typeLower.includes('compose')) {
      queriesToInvalidate.push(['compose', 'projects']);
      if (event.projectName) {
        queriesToInvalidate.push(['compose', 'project', event.projectName]);
      }
    }

    if (typeLower.includes('container')) {
      queriesToInvalidate.push(['containers']);
    }

    queriesToInvalidate.push(['dashboard']);

    if (queriesToInvalidate.length > 0) {
      scheduleRefetch(queriesToInvalidate);
    }
  });
  unsubscribers.push(unsubOperation);

  // Handle reconnection - immediately refresh ALL data
  const unsubReconnected = onReconnected(() => {
    logger.log('[Bridge] Reconnected - refreshing all data immediately');

    queryClient.refetchQueries({ queryKey: ['containers'], exact: false });
    queryClient.refetchQueries({ queryKey: ['compose'], exact: false });
    queryClient.refetchQueries({ queryKey: ['dashboard'], exact: false });
    queryClient.refetchQueries({ queryKey: ['images'], exact: false });
  });
  unsubscribers.push(unsubReconnected);

  logger.log('[Bridge] SSE-Query bridge initialized');

  // Return cleanup function
  return () => {
    logger.log('[Bridge] Cleaning up SSE-Query bridge');
    unsubscribers.forEach(unsub => unsub());
    recentComposeEvents.clear();
    throttleTrackers.clear();
    clearAllCrashLooping();
  };
}
