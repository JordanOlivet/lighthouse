<script lang="ts">
  import { untrack } from 'svelte';
  import { useQueryClient } from '@tanstack/svelte-query';
  import { X, Download, Package, Loader2, CheckCircle2, AlertCircle, Clock, ArrowDownToLine, Archive, RotateCw, ChevronDown, ChevronUp } from 'lucide-svelte';
  import { toast } from 'svelte-sonner';
  import { t } from '$lib/i18n';
  import { updateApi } from '$lib/api/update';
  import Badge from '$lib/components/ui/badge.svelte';
  import Checkbox from '$lib/components/ui/checkbox.svelte';
  import { projectUpdateState, markProjectAsUpdated } from '$lib/stores/projectUpdate.svelte';
  import { startBatchOperation } from '$lib/stores/batchOperation.svelte';
  import { onPullProgressUpdate } from '$lib/stores/sse.svelte';
  import type { ProjectUpdateSummary, UpdateProgressEvent, ServicePullStatus, ProjectUpdateRequest } from '$lib/types/update';
  import { withConnectionLossRecovery, ConnectionLostError } from '$lib/utils/connectionLoss';
  import { operationRecoveryDeps } from '$lib/api/operationRecovery';
  import { OperationType } from '$lib/types';

  interface Props {
    open: boolean;
    onClose: () => void;
    updateFn?: (projectName: string, options: ProjectUpdateRequest) => Promise<unknown>;
  }

  let { open, onClose, updateFn }: Props = $props();

  const queryClient = useQueryClient();

  // State for project selection
  let selectedProjects = $state<Set<string>>(new Set());

  // State for "restart after update" option
  let restartAfterUpdate = $state(true);

  // State for "restart full project" option
  let restartFullProject = $state(true);

  // State for tracking update progress
  let updatingProjects = $state<Set<string>>(new Set());
  let completedProjects = $state<Set<string>>(new Set());
  let failedProjects = $state<Map<string, string>>(new Map());

  // Frozen project list captured at update start to prevent UI reset
  let frozenProjects = $state<ProjectUpdateSummary[]>([]);

  // SSE real-time progress for the currently-updating project
  let currentProjectProgress = $state<UpdateProgressEvent | null>(null);
  let currentProjectLogs = $state<string[]>([]);
  let logsExpanded = $state(false);
  let logsContainer = $state<HTMLDivElement | null>(null);
  let currentProjectIndex = $state(0);
  let totalProjectsToUpdate = $state(0);
  let unsubscribePullProgress: (() => void) | null = null;

  // Get projects with updates from the store
  const projectsWithUpdates = $derived(
    projectUpdateState.checkResult?.projects.filter(p => p.servicesWithUpdates > 0) ?? []
  );

  // Display list: frozen during update so rows stay visible even after markProjectAsUpdated
  const displayProjects = $derived(
    frozenProjects.length > 0 ? frozenProjects : projectsWithUpdates
  );

  // Initialize selection when dialog opens — untrack prevents re-running on projectsWithUpdates changes
  $effect(() => {
    if (open) {
      const projects = untrack(() =>
        projectUpdateState.checkResult?.projects.filter(p => p.servicesWithUpdates > 0) ?? []
      );
      selectedProjects = new Set(projects.map(p => p.projectName));
      updatingProjects = new Set();
      completedProjects = new Set();
      failedProjects = new Map();
      // Set restartAfterUpdate based on whether any project has running services
      const hasRunning = projects.some(p => p.hasRunningServices);
      restartAfterUpdate = hasRunning;
      restartFullProject = true;
      frozenProjects = [];
      currentProjectProgress = null;
      currentProjectLogs = [];
      logsExpanded = false;
      currentProjectIndex = 0;
      totalProjectsToUpdate = 0;
    }
  });

  // Cleanup SSE subscription if dialog closes while updating
  $effect(() => {
    if (!open && unsubscribePullProgress) {
      unsubscribePullProgress();
      unsubscribePullProgress = null;
    }
  });

  // Auto-scroll logs when new entries are added
  $effect(() => {
    if (logsContainer && currentProjectLogs.length > 0) {
      logsContainer.scrollTop = logsContainer.scrollHeight;
    }
  });

  // Computed values
  const allSelected = $derived(
    displayProjects.length > 0 &&
    displayProjects.every(p => selectedProjects.has(p.projectName))
  );

  const noneSelected = $derived(selectedProjects.size === 0);

  const isUpdating = $derived(updatingProjects.size > 0);

  const totalServicesSelected = $derived(
    displayProjects
      .filter(p => selectedProjects.has(p.projectName))
      .reduce((sum, p) => sum + p.servicesWithUpdates, 0)
  );

  // Functions
  function toggleProject(projectName: string) {
    if (selectedProjects.has(projectName)) {
      selectedProjects.delete(projectName);
    } else {
      selectedProjects.add(projectName);
    }
    selectedProjects = new Set(selectedProjects);
  }

  function selectAll() {
    selectedProjects = new Set(projectsWithUpdates.map(p => p.projectName));
  }

  function deselectAll() {
    selectedProjects = new Set();
  }

  async function handleUpdateSelected() {
    if (noneSelected || isUpdating) return;

    const projectsToUpdate = Array.from(selectedProjects);

    // Freeze the project list (shallow-copy each object to preserve servicesWithUpdates count)
    frozenProjects = projectsWithUpdates
      .filter(p => selectedProjects.has(p.projectName))
      .map(p => ({ ...p }));
    totalProjectsToUpdate = projectsToUpdate.length;
    currentProjectIndex = 0;

    // Start a batch operation to suppress SignalR-triggered refreshes during updates
    const batchOperationId = `bulk-update-${Date.now()}`;
    const endBatchOp = startBatchOperation(batchOperationId);

    try {
      // Update projects sequentially
      for (const projectName of projectsToUpdate) {
        currentProjectIndex++;
        currentProjectProgress = null;
        currentProjectLogs = [];
        logsExpanded = false;
        updatingProjects.add(projectName);
        updatingProjects = new Set(updatingProjects);

        // Subscribe to SSE pull progress for this project
        unsubscribePullProgress = onPullProgressUpdate((event) => {
          if (event.projectName === projectName) {
            currentProjectProgress = event;
            if (event.currentLog) {
              currentProjectLogs = [...currentProjectLogs, event.currentLog].slice(-100);
            }
          }
        });

        try {
          const doUpdate = updateFn ?? updateApi.updateProject;
          // Updating the reverse proxy that serves Lighthouse cuts this request: recover the
          // real outcome from the operation history (and wait for the backend to be reachable
          // again) before moving on to the next project.
          await withConnectionLossRecovery(
            () => doUpdate(projectName, { updateAll: true, restartFullProject, restartAfterUpdate }),
            {
              type: OperationType.ComposeUpdate,
              projectName,
              onConnectionLost: () => toast.info($t('compose.connectionLostChecking'))
            },
            operationRecoveryDeps
          );

          completedProjects.add(projectName);
          completedProjects = new Set(completedProjects);

          // Mark as updated in the store
          markProjectAsUpdated(projectName);
        } catch (error) {
          const errorMessage = error instanceof ConnectionLostError
            ? $t('compose.connectionLostUnknown')
            : error instanceof Error ? error.message : 'Unknown error';
          failedProjects.set(projectName, errorMessage);
          failedProjects = new Map(failedProjects);
        } finally {
          updatingProjects.delete(projectName);
          updatingProjects = new Set(updatingProjects);
          if (unsubscribePullProgress) {
            unsubscribePullProgress();
            unsubscribePullProgress = null;
          }
        }
      }
    } finally {
      endBatchOp();
      currentProjectProgress = null;
    }

    // Invalidate queries to refresh project data (only once at the end)
    queryClient.invalidateQueries({ queryKey: ['compose', 'projects'] });
    queryClient.invalidateQueries({ queryKey: ['containers'] });

    // Show result toast
    const successCount = completedProjects.size;
    const failedCount = failedProjects.size;

    if (failedCount === 0) {
      toast.success($t('update.bulkUpdateSuccess').replace('{count}', successCount.toString()));
      onClose();
    } else if (successCount > 0) {
      toast.warning(
        $t('update.bulkUpdatePartial')
          .replace('{success}', successCount.toString())
          .replace('{failed}', failedCount.toString())
      );
    } else {
      toast.error($t('update.bulkUpdateFailed'));
    }
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape' && open && !isUpdating) {
      onClose();
    }
  }

  function handleBackdropClick(e: MouseEvent) {
    if (e.target === e.currentTarget && !isUpdating) {
      onClose();
    }
  }

  function getProjectStatus(projectName: string): 'pending' | 'updating' | 'completed' | 'failed' {
    if (completedProjects.has(projectName)) return 'completed';
    if (failedProjects.has(projectName)) return 'failed';
    if (updatingProjects.has(projectName)) return 'updating';
    return 'pending';
  }

  function getStatusIcon(status: ServicePullStatus) {
    switch (status) {
      case 'waiting': return Clock;
      case 'pulling': return Loader2;
      case 'downloading': return ArrowDownToLine;
      case 'extracting': return Archive;
      case 'pulled': return CheckCircle2;
      case 'recreating': return RotateCw;
      case 'completed': return CheckCircle2;
      case 'error': return AlertCircle;
      default: return Clock;
    }
  }

  function getStatusColor(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting': return 'text-gray-400 dark:text-gray-500';
      case 'pulling':
      case 'downloading':
      case 'extracting': return 'text-blue-500 dark:text-blue-400';
      case 'pulled':
      case 'completed': return 'text-green-500 dark:text-green-400';
      case 'recreating': return 'text-amber-500 dark:text-amber-400';
      case 'error': return 'text-red-500 dark:text-red-400';
      default: return 'text-gray-400 dark:text-gray-500';
    }
  }

  function getStatusBgColor(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting': return 'bg-gray-100 dark:bg-gray-700';
      case 'pulling':
      case 'downloading':
      case 'extracting': return 'bg-blue-100 dark:bg-blue-900/30';
      case 'pulled':
      case 'completed': return 'bg-green-100 dark:bg-green-900/30';
      case 'recreating': return 'bg-amber-100 dark:bg-amber-900/30';
      case 'error': return 'bg-red-100 dark:bg-red-900/30';
      default: return 'bg-gray-100 dark:bg-gray-700';
    }
  }

  function getStatusLabel(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting': return $t('update.progress.waiting');
      case 'pulling': return $t('update.progress.pulling');
      case 'downloading': return $t('update.progress.downloading');
      case 'extracting': return $t('update.progress.extracting');
      case 'pulled': return $t('update.progress.pulled');
      case 'recreating': return $t('update.progress.recreating');
      case 'completed': return $t('update.progress.completed');
      case 'error': return $t('update.progress.error');
      default: return status;
    }
  }

  function getProgressBarColor(status: ServicePullStatus): string {
    switch (status) {
      case 'error': return 'bg-red-500';
      case 'pulled':
      case 'completed': return 'bg-green-500';
      case 'recreating': return 'bg-amber-500';
      default: return 'bg-blue-500';
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

{#if open}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
    onclick={handleBackdropClick}
  >
    <div class="relative bg-white dark:bg-gray-800 rounded-xl shadow-2xl max-w-lg w-full mx-4 max-h-[90vh] overflow-hidden flex flex-col">
      <!-- Header -->
      <div class="px-6 py-4 border-b border-gray-200 dark:border-gray-700 flex items-center justify-between">
        <div>
          <h2 class="text-lg font-semibold text-gray-900 dark:text-white">
            {$t('update.updateAllProjects')}
          </h2>
          <p class="text-sm text-gray-500 dark:text-gray-400 mt-0.5">
            {#if isUpdating}
              {$t('update.updatingProject')} {currentProjectIndex} / {totalProjectsToUpdate}
            {:else}
              {projectsWithUpdates.length} {$t('update.projectsHaveUpdates')}
            {/if}
          </p>
        </div>
        <button
          class="p-2 rounded-lg text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors disabled:opacity-50 cursor-pointer"
          onclick={onClose}
          disabled={isUpdating}
        >
          <X class="w-5 h-5" />
        </button>
      </div>

      <!-- Content -->
      <div class="flex-1 overflow-y-auto p-6">
        {#if projectsWithUpdates.length === 0 && frozenProjects.length === 0}
          <div class="text-center py-8 text-gray-500 dark:text-gray-400">
            <Package class="w-12 h-12 mx-auto mb-3 opacity-50" />
            <p>{$t('update.allProjectsUpToDate')}</p>
          </div>
        {:else}
          <!-- Select All / Deselect All -->
          {#if projectsWithUpdates.length > 1 && !isUpdating}
            <div class="flex gap-2 mb-4">
              <button
                class="px-3 py-1.5 text-xs font-medium rounded-lg bg-blue-50 dark:bg-blue-900/20 text-blue-700 dark:text-blue-300 hover:bg-blue-100 dark:hover:bg-blue-900/30 transition-colors cursor-pointer"
                onclick={selectAll}
              >
                {$t('common.selectAll')}
              </button>
              <button
                class="px-3 py-1.5 text-xs font-medium rounded-lg bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300 hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors cursor-pointer"
                onclick={deselectAll}
              >
                {$t('common.deselectAll')}
              </button>
            </div>
          {/if}

          <!-- Projects List -->
          <div class="space-y-2">
            {#each displayProjects as project (project.projectName)}
              {@const isSelected = selectedProjects.has(project.projectName)}
              {@const status = getProjectStatus(project.projectName)}

              <div
                class="p-3 rounded-lg border transition-all {
                  status === 'completed'
                    ? 'border-green-300 dark:border-green-600 bg-green-50/50 dark:bg-green-900/20'
                    : status === 'failed'
                      ? 'border-red-300 dark:border-red-600 bg-red-50/50 dark:bg-red-900/20'
                      : status === 'updating'
                        ? 'border-blue-300 dark:border-blue-600 bg-blue-50/50 dark:bg-blue-900/20'
                        : isSelected
                          ? 'border-blue-200 dark:border-blue-700 bg-blue-50/30 dark:bg-blue-900/10'
                          : 'border-gray-200 dark:border-gray-700 hover:border-gray-300 dark:hover:border-gray-600'
                }"
              >
                <div class="flex items-center gap-3">
                  <!-- Checkbox or Status Icon -->
                  {#if status === 'updating'}
                    <Loader2 class="w-4 h-4 animate-spin text-blue-600 dark:text-blue-400 shrink-0" />
                  {:else if status === 'completed'}
                    <CheckCircle2 class="w-4 h-4 text-green-600 dark:text-green-400 shrink-0" />
                  {:else if status === 'failed'}
                    <AlertCircle class="w-4 h-4 text-red-600 dark:text-red-400 shrink-0" />
                  {:else}
                    <Checkbox
                      checked={isSelected}
                      onclick={() => toggleProject(project.projectName)}
                      disabled={isUpdating}
                    />
                  {/if}

                  <!-- Project Info -->
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2">
                      <span class="font-medium text-gray-900 dark:text-white truncate">
                        {project.projectName}
                      </span>
                      <Badge class="bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300 text-xs shrink-0">
                        {project.servicesWithUpdates} {$t('update.servicesNeedUpdate')}
                      </Badge>
                    </div>

                    {#if status === 'failed'}
                      <p class="text-xs text-red-600 dark:text-red-400 mt-1">
                        {failedProjects.get(project.projectName)}
                      </p>
                    {/if}
                  </div>
                </div>

                <!-- Real-time progress (shown only for the currently-updating project) -->
                {#if status === 'updating'}
                  <div class="mt-3 space-y-2 pl-7">
                    <!-- Overall progress bar -->
                    <div class="space-y-1">
                      <div class="flex justify-between text-xs text-gray-500 dark:text-gray-400">
                        <span>{currentProjectProgress?.phase === 'pull' ? $t('update.phase.pull') : $t('update.phase.recreate')}</span>
                        <span>{currentProjectProgress?.overallProgress ?? 0}%</span>
                      </div>
                      <div class="h-2 bg-gray-200 dark:bg-gray-700 rounded-full overflow-hidden">
                        <div
                          class="h-full bg-blue-500 rounded-full transition-all duration-300"
                          style="width: {currentProjectProgress?.overallProgress ?? 0}%"
                        ></div>
                      </div>
                    </div>

                    <!-- Per-service progress -->
                    {#if currentProjectProgress?.services?.length}
                      <div class="space-y-1.5 mt-2">
                        {#each currentProjectProgress.services as service (service.serviceName)}
                          {@const StatusIcon = getStatusIcon(service.status)}
                          <div class="flex items-center gap-2 text-xs">
                            <StatusIcon class="w-3.5 h-3.5 shrink-0 {getStatusColor(service.status)} {['pulling','downloading','extracting','recreating'].includes(service.status) ? 'animate-spin' : ''}" />
                            <span class="font-medium text-gray-700 dark:text-gray-300 w-24 truncate">{service.serviceName}</span>
                            <Badge class="{getStatusBgColor(service.status)} {getStatusColor(service.status)} text-xs shrink-0">
                              {getStatusLabel(service.status)}
                            </Badge>
                            {#if ['downloading','extracting','pulling'].includes(service.status)}
                              <div class="flex-1 h-1.5 bg-gray-200 dark:bg-gray-700 rounded-full overflow-hidden">
                                <div
                                  class="h-full {getProgressBarColor(service.status)} rounded-full transition-all duration-200"
                                  style="width: {service.progressPercent}%"
                                ></div>
                              </div>
                              <span class="text-gray-500 shrink-0">{service.progressPercent}%</span>
                            {/if}
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <!-- Fallback spinner while waiting for first SSE event -->
                      <div class="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
                        <Loader2 class="w-3.5 h-3.5 animate-spin" />
                        <span>{$t('update.progress.waiting')}...</span>
                      </div>
                    {/if}

                    <!-- Collapsible logs -->
                    {#if currentProjectLogs.length > 0}
                      <div class="mt-1 border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden">
                        <button
                          class="w-full px-3 py-1.5 flex items-center justify-between bg-gray-100 dark:bg-gray-700/50 transition-colors cursor-pointer"
                          onclick={() => logsExpanded = !logsExpanded}
                        >
                          <span class="text-xs font-medium text-gray-600 dark:text-gray-400">
                            {$t('update.logs')} ({currentProjectLogs.length})
                          </span>
                          {#if logsExpanded}
                            <ChevronUp class="w-3.5 h-3.5 text-gray-500" />
                          {:else}
                            <ChevronDown class="w-3.5 h-3.5 text-gray-500" />
                          {/if}
                        </button>
                        {#if logsExpanded}
                          <div
                            bind:this={logsContainer}
                            class="max-h-32 overflow-y-auto p-2 bg-gray-900 text-gray-100 font-mono text-xs"
                          >
                            {#each currentProjectLogs as log, i (i)}
                              <div class="whitespace-pre-wrap break-all">{log}</div>
                            {/each}
                          </div>
                        {/if}
                      </div>
                    {/if}
                  </div>
                {/if}
              </div>
            {/each}
          </div>
        {/if}
      </div>

      <!-- Footer -->
      <div class="px-6 py-4 border-t border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800/50 flex flex-col gap-3">
        {#if isUpdating}
          <div class="flex items-center justify-between">
            <div class="text-sm text-gray-500 dark:text-gray-400">
              {completedProjects.size + failedProjects.size} / {totalProjectsToUpdate} {$t('update.projectsCompleted')}
            </div>
            <div class="flex items-center gap-2 text-sm text-blue-600 dark:text-blue-400">
              <Loader2 class="w-4 h-4 animate-spin" />
              {$t('update.updating')}
            </div>
          </div>
        {:else}
          <div class="flex items-center justify-between">
            <p class="text-sm text-gray-500 dark:text-gray-400">
              {selectedProjects.size} {$t('update.projectsSelected')} ({totalServicesSelected} {$t('compose.services').toLowerCase()})
            </p>
            <div class="flex gap-3">
              <button
                class="px-4 py-2 text-sm font-medium rounded-lg bg-gray-200 dark:bg-gray-700 text-gray-700 dark:text-gray-300 hover:bg-gray-300 dark:hover:bg-gray-600 transition-colors disabled:opacity-50"
                onclick={onClose}
                disabled={isUpdating}
              >
                {$t('common.cancel')}
              </button>
              <button
                class="px-4 py-2 text-sm font-medium rounded-lg bg-blue-600 text-white hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
                onclick={handleUpdateSelected}
                disabled={noneSelected || isUpdating}
              >
                <Download class="w-4 h-4" />
                {$t('update.updateSelected')}
              </button>
            </div>
          </div>
          <!-- Restart after update checkbox -->
          <label class="flex items-center gap-2 cursor-pointer">
            <Checkbox
              checked={restartAfterUpdate}
              onclick={() => restartAfterUpdate = !restartAfterUpdate}
            />
            <div class="flex flex-col">
              <span class="text-sm text-gray-700 dark:text-gray-300">{$t('update.restartAfterUpdate')}</span>
              <span class="text-xs text-gray-500 dark:text-gray-400">{$t('update.restartAfterUpdateHint')}</span>
            </div>
          </label>
          <!-- Restart full project checkbox (only enabled if restartAfterUpdate is true) -->
          <label class="flex items-center gap-2 {restartAfterUpdate ? 'cursor-pointer' : 'cursor-not-allowed opacity-50'}">
            <Checkbox
              checked={restartFullProject}
              onclick={() => restartAfterUpdate && (restartFullProject = !restartFullProject)}
              disabled={!restartAfterUpdate}
            />
            <div class="flex flex-col">
              <span class="text-sm text-gray-700 dark:text-gray-300">{$t('update.restartFullProject')}</span>
              <span class="text-xs text-gray-500 dark:text-gray-400">{$t('update.restartFullProjectHint')}</span>
            </div>
          </label>
        {/if}
      </div>
    </div>
  </div>
{/if}
