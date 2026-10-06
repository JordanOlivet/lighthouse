<script lang="ts">
  import { X, Trash2, CheckCircle2, XCircle, Loader2, MinusCircle, ChevronDown, ChevronRight, Clock, User, CheckCheck, Eye } from 'lucide-svelte';
  import {
    actionLogState,
    filteredEntries,
    unseenFailureCount,
    closePanel,
    setStatusFilter,
    clearHistory,
    acknowledgeOperation,
    acknowledgeAll,
    type StatusFilter,
  } from '$lib/stores/actionLog.svelte';
  import { operationsApi } from '$lib/api/operations';
  import type { OperationDetails } from '$lib/types';
  import { t } from '$lib/i18n';
  import { tick, untrack } from 'svelte';

  interface Props {
    // Overlay: fixed drawer over the content (below lg). Otherwise docked beside it.
    overlay?: boolean;
  }

  let { overlay = false }: Props = $props();

  let expandedIds = $state<Set<string>>(new Set());
  let loadedDetails = $state<Record<string, OperationDetails>>({});
  let loadingDetails = $state<Record<string, boolean>>({});
  let panelEl: HTMLElement | undefined = $state();

  const statusFilters: StatusFilter[] = ['all', 'running', 'failed', 'completed'];

  function getOperationLabel(type: string): string {
    const labels: Record<string, string> = {
      compose_up: $t('actionLog.composeUp'),
      compose_down: $t('actionLog.composeDown'),
      compose_start: $t('actionLog.composeStart'),
      compose_stop: $t('actionLog.composeStop'),
      compose_restart: $t('actionLog.composeRestart'),
      compose_build: $t('actionLog.composeBuild'),
      compose_pull: $t('actionLog.composePull'),
      container_start: $t('actionLog.containerStart'),
      container_stop: $t('actionLog.containerStop'),
      container_restart: $t('actionLog.containerRestart'),
      container_remove: $t('actionLog.containerRemove'),
      compose_update: $t('actionLog.composeUpdate'),
      container_update: $t('actionLog.containerUpdate'),
    };
    return labels[type] ?? type;
  }

  function getEntityName(entry: { projectPath?: string; projectName?: string; containerName?: string; containerId?: string }): string {
    if (entry.projectPath) {
      const segments = entry.projectPath.replace(/\\/g, '/').replace(/\/+$/, '').split('/');
      return segments[segments.length - 1] || entry.projectName || '';
    }
    return entry.projectName ?? entry.containerName ?? entry.containerId ?? '';
  }

  function getRelativeTime(dateStr: string): string {
    const diff = Date.now() - new Date(ensureUtc(dateStr)).getTime();
    const seconds = Math.floor(diff / 1000);
    if (seconds < 60) return $t('actionLog.justNow');
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return $t('actionLog.minutesAgo', { count: minutes });
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return $t('actionLog.hoursAgo', { count: hours });
    const days = Math.floor(hours / 24);
    return $t('actionLog.daysAgo', { count: days });
  }

  function ensureUtc(dateStr: string): string {
    // Backend sends UTC dates without 'Z' suffix — normalize so the browser
    // doesn't interpret them as local time.
    if (!dateStr.endsWith('Z') && !dateStr.includes('+') && !dateStr.includes('T')) {
      return dateStr + 'Z';
    }
    if (dateStr.includes('T') && !dateStr.endsWith('Z') && !dateStr.includes('+') && !dateStr.includes('-', dateStr.indexOf('T'))) {
      return dateStr + 'Z';
    }
    return dateStr;
  }

  function getDuration(startedAt: string, completedAt?: string): string | null {
    if (!completedAt) return null;
    const ms = Math.abs(new Date(ensureUtc(completedAt)).getTime() - new Date(ensureUtc(startedAt)).getTime());
    if (ms < 1000) return `${ms}ms`;
    const seconds = Math.floor(ms / 1000);
    if (seconds < 60) return `${seconds}s`;
    const minutes = Math.floor(seconds / 60);
    const remainingSeconds = seconds % 60;
    return `${minutes}m ${remainingSeconds}s`;
  }

  async function toggleExpand(operationId: string) {
    if (expandedIds.has(operationId)) {
      expandedIds.delete(operationId);
      expandedIds = new Set(expandedIds);
      return;
    }
    expandedIds.add(operationId);
    expandedIds = new Set(expandedIds);
    await loadDetails(operationId);
  }

  async function loadDetails(operationId: string) {
    if (!loadedDetails[operationId] && !loadingDetails[operationId]) {
      loadingDetails[operationId] = true;
      try {
        const details = await operationsApi.getOperation(operationId);
        loadedDetails[operationId] = details;
      } catch {
        // silently fail
      } finally {
        loadingDetails[operationId] = false;
      }
    }
  }

  // Auto-scroll to selected operation and expand it
  $effect(() => {
    const selectedId = actionLogState.selectedOperationId;
    if (selectedId && panelEl) {
      untrack(() => {
        // Expand and load details
        expandedIds.add(selectedId);
        expandedIds = new Set(expandedIds);
        loadDetails(selectedId);
      });

      tick().then(() => {
        const el = panelEl?.querySelector(`[data-operation-id="${selectedId}"]`);
        if (el) {
          el.scrollIntoView({ behavior: 'smooth', block: 'center' });
          el.classList.add('highlight-flash');
          setTimeout(() => el.classList.remove('highlight-flash'), 2000);
        }
        actionLogState.selectedOperationId = null;
      });
    }
  });
</script>

<aside
  bind:this={panelEl}
  class="border-l border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 flex flex-col overflow-hidden {overlay
    ? 'fixed inset-y-0 right-0 z-[120] w-full max-w-[400px] shadow-xl'
    : 'w-[400px] shrink-0'}"
>
  <!-- Header -->
  <div class="flex items-center justify-between px-4 py-3 border-b border-gray-200 dark:border-gray-700">
    <h2 class="text-sm font-semibold text-gray-800 dark:text-gray-200">
      {$t('actionLog.title')}
    </h2>
    <div class="flex items-center gap-1">
      {#if unseenFailureCount.current > 0}
        <button
          onclick={acknowledgeAll}
          class="p-1 rounded hover:bg-green-50 dark:hover:bg-green-900/20 cursor-pointer"
          title={$t('actionLog.markAllSeen')}
        >
          <CheckCheck class="w-4 h-4 text-gray-400 hover:text-green-500" />
        </button>
      {/if}
      {#if actionLogState.entries.length > 0}
        <button
          onclick={clearHistory}
          class="p-1 rounded hover:bg-red-50 dark:hover:bg-red-900/20 cursor-pointer"
          title={$t('common.clear')}
        >
          <Trash2 class="w-4 h-4 text-gray-400 hover:text-red-500" />
        </button>
      {/if}
      <button
        onclick={closePanel}
        class="p-1 rounded hover:bg-gray-100 dark:hover:bg-gray-700 cursor-pointer"
        aria-label={$t('common.close')}
      >
        <X class="w-4 h-4 text-gray-500" />
      </button>
    </div>
  </div>

  <!-- Status filter -->
  <div class="flex gap-1 px-4 py-2 border-b border-gray-100 dark:border-gray-700">
    {#each statusFilters as filter}
      <button
        onclick={() => setStatusFilter(filter)}
        class="px-2 py-1 text-xs rounded-md transition-colors cursor-pointer {actionLogState.statusFilter === filter
          ? 'bg-blue-100 dark:bg-blue-900 text-blue-700 dark:text-blue-300 font-medium'
          : 'text-gray-500 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-700'}"
      >
        {$t(`actionLog.${filter}`)}
      </button>
    {/each}
  </div>

  <!-- Entries list -->
  <div class="flex-1 overflow-y-auto">
    {#if actionLogState.isLoading}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="w-5 h-5 animate-spin text-gray-400" />
      </div>
    {:else if filteredEntries.current.length === 0}
      <div class="text-center py-8 text-sm text-gray-400">
        {$t('actionLog.noActions')}
      </div>
    {:else}
      {#each filteredEntries.current as entry (entry.operationId)}
        <div
          data-operation-id={entry.operationId}
          class="border-b border-gray-50 dark:border-gray-700/50 transition-colors"
        >
          <!-- Entry header -->
          <button
            onclick={() => toggleExpand(entry.operationId)}
            class="w-full flex items-start gap-3 px-4 py-3 hover:bg-gray-50 dark:hover:bg-gray-750 text-left cursor-pointer"
          >
            <!-- Status icon -->
            <div class="mt-0.5 shrink-0">
              {#if entry.status === 'running' || entry.status === 'pending'}
                <Loader2 class="w-4 h-4 animate-spin text-blue-500" />
              {:else if entry.status === 'completed'}
                <CheckCircle2 class="w-4 h-4 text-green-500" />
              {:else if entry.status === 'failed'}
                <XCircle class="w-4 h-4 text-red-500" />
              {:else}
                <MinusCircle class="w-4 h-4 text-gray-400" />
              {/if}
            </div>

            <!-- Content -->
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2">
                <span class="text-sm font-medium text-gray-800 dark:text-gray-200 truncate">
                  {getOperationLabel(entry.type)}
                </span>
                {#if expandedIds.has(entry.operationId)}
                  <ChevronDown class="w-3 h-3 text-gray-400 shrink-0" />
                {:else}
                  <ChevronRight class="w-3 h-3 text-gray-400 shrink-0" />
                {/if}
              </div>
              <div class="text-xs text-gray-500 dark:text-gray-400 truncate">
                {getEntityName(entry)}
              </div>
            </div>

            <!-- Timestamp & actions -->
            <div class="flex items-center gap-1 shrink-0">
              <span class="text-xs text-gray-400 whitespace-nowrap">
                {getRelativeTime(entry.startedAt)}
              </span>
              {#if entry.status === 'failed' && !entry.isAcknowledged}
                <span
                  role="button"
                  tabindex="0"
                  onclick={(e) => { e.stopPropagation(); acknowledgeOperation(entry.operationId); }}
                  onkeydown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      e.stopPropagation();
                      acknowledgeOperation(entry.operationId);
                    }
                  }}
                  class="p-0.5 rounded hover:bg-green-50 dark:hover:bg-green-900/20 cursor-pointer inline-flex items-center"
                  title={$t('actionLog.markSeen')}
                >
                  <Eye class="w-3.5 h-3.5 text-gray-400 hover:text-green-500" />
                </span>
              {/if}
            </div>
          </button>

          <!-- Expanded details -->
          {#if expandedIds.has(entry.operationId)}
            <div class="px-4 pb-3 pl-11 space-y-2">
              {#if loadingDetails[entry.operationId]}
                <div class="flex items-center gap-2 text-xs text-gray-400">
                  <Loader2 class="w-3 h-3 animate-spin" />
                  {$t('common.loading')}
                </div>
              {:else}
                {#if entry.errorMessage || loadedDetails[entry.operationId]?.errorMessage}
                  <div class="text-xs text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-900/20 rounded p-2 break-words">
                    {entry.errorMessage || loadedDetails[entry.operationId]?.errorMessage}
                  </div>
                {/if}

                {#if loadedDetails[entry.operationId]?.logs}
                  <details>
                    <summary class="text-xs text-gray-500 cursor-pointer hover:text-gray-700 dark:hover:text-gray-300">
                      {$t('actionLog.showLogs')}
                    </summary>
                    <pre class="mt-1 text-xs bg-gray-50 dark:bg-gray-900 rounded p-2 max-h-40 overflow-y-auto whitespace-pre-wrap break-words text-gray-600 dark:text-gray-400">{loadedDetails[entry.operationId]?.logs}</pre>
                  </details>
                {/if}

                <div class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-400">
                  {#if getDuration(entry.startedAt, entry.completedAt ?? loadedDetails[entry.operationId]?.completedAt)}
                    <span class="flex items-center gap-1">
                      <Clock class="w-3 h-3" />
                      {getDuration(entry.startedAt, entry.completedAt ?? loadedDetails[entry.operationId]?.completedAt)}
                    </span>
                  {/if}
                  {#if entry.username || loadedDetails[entry.operationId]?.username}
                    <span class="flex items-center gap-1">
                      <User class="w-3 h-3" />
                      {entry.username || loadedDetails[entry.operationId]?.username}
                    </span>
                  {/if}
                </div>
              {/if}
            </div>
          {/if}
        </div>
      {/each}
    {/if}
  </div>
</aside>

<style>
  :global(.highlight-flash) {
    animation: flash 2s ease-out;
  }

  @keyframes flash {
    0%, 20% { background-color: rgba(59, 130, 246, 0.15); }
    100% { background-color: transparent; }
  }
</style>
