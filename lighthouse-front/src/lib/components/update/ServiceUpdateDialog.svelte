<script lang="ts">
  import { createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { X, Download, AlertCircle, CheckCircle2, Package, HardDrive, Pin, Loader2, Copy, Check, ChevronDown, ChevronUp, Clock, ArrowDownToLine, Archive, RotateCw } from 'lucide-svelte';
  import { toast } from 'svelte-sonner';
  import { t } from '$lib/i18n';
  import { updateApi } from '$lib/api/update';
  import Badge from '$lib/components/ui/badge.svelte';
  import Checkbox from '$lib/components/ui/checkbox.svelte';
  import type { ProjectUpdateCheckResponse, ImageUpdateStatus, UpdateProgressEvent, ServicePullProgress, ServicePullStatus } from '$lib/types/update';
  import { markProjectAsUpdated } from '$lib/stores/projectUpdate.svelte';
  import { onPullProgressUpdate } from '$lib/stores/sse.svelte';
  import { startBatchOperation } from '$lib/stores/batchOperation.svelte';
  import { logger } from '$lib/utils/logger';
  import { withConnectionLossRecovery, ConnectionLostError } from '$lib/utils/connectionLoss';
  import { operationRecoveryDeps } from '$lib/api/operationRecovery';
  import { OperationType } from '$lib/types';

  interface Props {
    open: boolean;
    projectName: string;
    updateCheck: ProjectUpdateCheckResponse | null;
    onClose: () => void;
  }

  let { open, projectName, updateCheck, onClose }: Props = $props();

  const queryClient = useQueryClient();

  // State for service selection
  let selectedServices = $state<Set<string>>(new Set());

  // State for "restart after update" option - default based on container state
  let restartAfterUpdate = $state(true);

  // State for "restart full project" option
  let restartFullProject = $state(true);

  // State for tracking copied digests (to show checkmark temporarily)
  let copiedDigests = $state<Set<string>>(new Set());

  // State for update progress tracking
  let isUpdating = $state(false);
  let updateProgress = $state<UpdateProgressEvent | null>(null);
  let updateLogs = $state<string[]>([]);
  let logsExpanded = $state(false);
  let logsContainer = $state<HTMLDivElement | null>(null);

  // Unsubscribe function for SSE pull progress updates
  let unsubscribePullProgress: (() => void) | null = null;

  // Cleanup function for batch operation
  let endBatchOp: (() => void) | null = null;

  // Initialize with services that have updates when updateCheck changes
  $effect(() => {
    if (updateCheck) {
      selectedServices = new Set(
        updateCheck.images
          .filter(img => img.updateAvailable && img.updatePolicy !== 'disabled')
          .map(img => img.serviceName)
      );
      // Set restartAfterUpdate based on whether any service is running
      const hasRunning = updateCheck.images.some(img => img.containerState?.toLowerCase() === 'running');
      restartAfterUpdate = hasRunning;
    }
  });

  // Reset progress state when dialog closes
  $effect(() => {
    if (!open) {
      isUpdating = false;
      updateProgress = null;
      updateLogs = [];
      logsExpanded = false;
      restartAfterUpdate = true;
      restartFullProject = true;
      if (unsubscribePullProgress) {
        unsubscribePullProgress();
        unsubscribePullProgress = null;
      }
      if (endBatchOp) {
        endBatchOp();
        endBatchOp = null;
      }
    }
  });

  // Auto-scroll logs when new entries are added
  $effect(() => {
    if (logsContainer && updateLogs.length > 0) {
      logsContainer.scrollTop = logsContainer.scrollHeight;
    }
  });

  // Mutation for updating services
  const updateMutation = createMutation(() => ({
    // Updating the reverse proxy that serves Lighthouse cuts this request (e.g. 520 from the
    // CDN): recover the real outcome from the operation history instead of a false failure.
    mutationFn: () => withConnectionLossRecovery(
      () => updateApi.updateProject(projectName, {
        services: Array.from(selectedServices),
        restartFullProject,
        restartAfterUpdate
      }),
      {
        type: OperationType.ComposeUpdate,
        projectName,
        onConnectionLost: () => toast.info($t('compose.connectionLostChecking'))
      },
      operationRecoveryDeps
    ),
    onMutate: () => {
      // Start tracking progress
      isUpdating = true;
      updateProgress = null;
      updateLogs = [];

      // Start batch operation to suppress SSE-triggered refreshes during update
      const operationId = `update-${projectName}-${Date.now()}`;
      endBatchOp = startBatchOperation(operationId, projectName);

      // Subscribe to SSE pull progress updates
      unsubscribePullProgress = onPullProgressUpdate((event) => {
        logger.debug('ServiceUpdateDialog received progress event:', event, 'Expected projectName:', projectName);
        if (event.projectName === projectName) {
          logger.debug('Progress event matches project, updating UI');
          updateProgress = event;
          if (event.currentLog) {
            updateLogs = [...updateLogs, event.currentLog].slice(-100); // Keep last 100 lines
          }
        }
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['compose', 'projects'] });
      queryClient.invalidateQueries({ queryKey: ['containers'] });
      markProjectAsUpdated(projectName);
      toast.success($t('update.updateSuccess').replace('{count}', selectedServices.size.toString()));

      // Keep showing progress for a moment before closing
      setTimeout(() => {
        onClose();
      }, 1500);
    },
    onError: (error: Error) => {
      if (error instanceof ConnectionLostError) {
        toast.warning($t('compose.connectionLostUnknown'));
        queryClient.invalidateQueries({ queryKey: ['compose', 'projects'] });
      } else {
        toast.error($t('update.updateFailed') + ': ' + error.message);
      }
      isUpdating = false;
    },
    onSettled: () => {
      // End batch operation to allow normal SSE event handling
      if (endBatchOp) {
        endBatchOp();
        endBatchOp = null;
      }

      // Cleanup subscription
      if (unsubscribePullProgress) {
        unsubscribePullProgress();
        unsubscribePullProgress = null;
      }
    }
  }));

  // Computed values
  const updatableServices = $derived(
    updateCheck?.images.filter(img =>
      img.updateAvailable && img.updatePolicy !== 'disabled'
    ) ?? []
  );

  const allSelected = $derived(
    updatableServices.length > 0 &&
    updatableServices.every(img => selectedServices.has(img.serviceName))
  );

  const noneSelected = $derived(selectedServices.size === 0);

  // Functions
  function toggleService(serviceName: string) {
    if (selectedServices.has(serviceName)) {
      selectedServices.delete(serviceName);
    } else {
      selectedServices.add(serviceName);
    }
    selectedServices = new Set(selectedServices);
  }

  function selectAll() {
    selectedServices = new Set(updatableServices.map(img => img.serviceName));
  }

  function deselectAll() {
    selectedServices = new Set();
  }

  function truncateDigest(digest: string | null): string {
    if (!digest) return '-';
    // sha256:abc123... -> sha256:abc123
    const parts = digest.split(':');
    if (parts.length === 2 && parts[1].length > 12) {
      return `${parts[0]}:${parts[1].substring(0, 12)}...`;
    }
    return digest;
  }

  function formatDate(dateString: string | null): string {
    if (!dateString) return '';
    try {
      const date = new Date(dateString);
      return date.toLocaleString(undefined, {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit'
      });
    } catch {
      return '';
    }
  }

  async function copyToClipboard(text: string, id: string) {
    try {
      await navigator.clipboard.writeText(text);
      copiedDigests.add(id);
      copiedDigests = new Set(copiedDigests);

      // Reset after 2 seconds
      setTimeout(() => {
        copiedDigests.delete(id);
        copiedDigests = new Set(copiedDigests);
      }, 2000);
    } catch {
      toast.error('Failed to copy to clipboard');
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

  function handleUpdate() {
    updateMutation.mutate();
  }

  function getStatusIcon(status: ServicePullStatus) {
    switch (status) {
      case 'waiting':
        return Clock;
      case 'pulling':
        return Loader2;
      case 'downloading':
        return ArrowDownToLine;
      case 'extracting':
        return Archive;
      case 'pulled':
        return CheckCircle2;
      case 'recreating':
        return RotateCw;
      case 'completed':
        return CheckCircle2;
      case 'error':
        return AlertCircle;
      default:
        return Clock;
    }
  }

  function getStatusColor(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting':
        return 'text-gray-400 dark:text-gray-500';
      case 'pulling':
      case 'downloading':
      case 'extracting':
        return 'text-blue-500 dark:text-blue-400';
      case 'pulled':
      case 'completed':
        return 'text-green-500 dark:text-green-400';
      case 'recreating':
        return 'text-amber-500 dark:text-amber-400';
      case 'error':
        return 'text-red-500 dark:text-red-400';
      default:
        return 'text-gray-400 dark:text-gray-500';
    }
  }

  function getStatusBgColor(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting':
        return 'bg-gray-100 dark:bg-gray-700';
      case 'pulling':
      case 'downloading':
      case 'extracting':
        return 'bg-blue-100 dark:bg-blue-900/30';
      case 'pulled':
      case 'completed':
        return 'bg-green-100 dark:bg-green-900/30';
      case 'recreating':
        return 'bg-amber-100 dark:bg-amber-900/30';
      case 'error':
        return 'bg-red-100 dark:bg-red-900/30';
      default:
        return 'bg-gray-100 dark:bg-gray-700';
    }
  }

  function getStatusLabel(status: ServicePullStatus): string {
    switch (status) {
      case 'waiting':
        return $t('update.progress.waiting');
      case 'pulling':
        return $t('update.progress.pulling');
      case 'downloading':
        return $t('update.progress.downloading');
      case 'extracting':
        return $t('update.progress.extracting');
      case 'pulled':
        return $t('update.progress.pulled');
      case 'recreating':
        return $t('update.progress.recreating');
      case 'completed':
        return $t('update.progress.completed');
      case 'error':
        return $t('update.progress.error');
      default:
        return status;
    }
  }

  function getProgressBarColor(status: ServicePullStatus): string {
    switch (status) {
      case 'error':
        return 'bg-red-500';
      case 'pulled':
      case 'completed':
        return 'bg-green-500';
      case 'recreating':
        return 'bg-amber-500';
      default:
        return 'bg-blue-500';
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

{#if open && updateCheck}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
    onclick={handleBackdropClick}
  >
    <div class="relative bg-white dark:bg-gray-800 rounded-xl shadow-2xl max-w-2xl w-full mx-4 max-h-[90vh] overflow-hidden flex flex-col">
      <!-- Header -->
      <div class="px-6 py-4 border-b border-gray-200 dark:border-gray-700 flex items-center justify-between">
        <div>
          <h2 class="text-lg font-semibold text-gray-900 dark:text-white">
            {#if isUpdating}
              {$t('update.updating')} - {projectName}
            {:else}
              {$t('update.checkUpdates')} - {projectName}
            {/if}
          </h2>
          <p class="text-sm text-gray-500 dark:text-gray-400 mt-0.5">
            {#if isUpdating && updateProgress}
              {updateProgress.phase === 'pull' ? $t('update.phase.pull') : $t('update.phase.recreate')}
            {:else}
              {updatableServices.length} {$t('update.updatesAvailable')}
            {/if}
          </p>
        </div>
        {#if !isUpdating}
          <button
            class="p-2 rounded-lg text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
            onclick={onClose}
          >
            <X class="w-5 h-5" />
          </button>
        {/if}
      </div>

      <!-- Content -->
      <div class="flex-1 overflow-y-auto p-6">
        {#if isUpdating}
          <!-- Progress View -->
          <div class="space-y-4">
            <!-- Overall Progress Bar -->
            <div class="space-y-2">
              <div class="flex items-center justify-between text-sm">
                <span class="text-gray-600 dark:text-gray-400">{$t('update.overallProgress')}</span>
                <span class="font-medium text-gray-900 dark:text-white">
                  {updateProgress?.overallProgress ?? 0}%
                </span>
              </div>
              <div class="h-3 bg-gray-200 dark:bg-gray-700 rounded-full overflow-hidden">
                <div
                  class="h-full bg-blue-500 rounded-full transition-all duration-300 ease-out"
                  style="width: {updateProgress?.overallProgress ?? 0}%"
                ></div>
              </div>
            </div>

            <!-- Per-Service Progress -->
            <div class="space-y-3 mt-6">
              <h3 class="text-sm font-medium text-gray-700 dark:text-gray-300">
                {$t('update.serviceProgress')}
              </h3>

              {#if updateProgress?.services}
                {#each updateProgress.services as service (service.serviceName)}
                  {@const StatusIcon = getStatusIcon(service.status)}
                  <div class="p-3 rounded-lg border border-gray-200 dark:border-gray-700 bg-gray-50/50 dark:bg-gray-800/50">
                    <div class="flex items-center gap-3">
                      <!-- Status Icon -->
                      <div class="shrink-0 {getStatusColor(service.status)}">
                        <StatusIcon class="w-5 h-5 {service.status === 'pulling' || service.status === 'downloading' || service.status === 'extracting' || service.status === 'recreating' ? 'animate-spin' : ''}" />
                      </div>

                      <!-- Service Name and Status -->
                      <div class="flex-1 min-w-0">
                        <div class="flex items-center gap-2">
                          <span class="font-medium text-gray-900 dark:text-white truncate">
                            {service.serviceName}
                          </span>
                          <Badge class="{getStatusBgColor(service.status)} {getStatusColor(service.status)} text-xs">
                            {getStatusLabel(service.status)}
                          </Badge>
                        </div>

                        <!-- Progress Bar for active downloads -->
                        {#if service.status === 'downloading' || service.status === 'extracting' || service.status === 'pulling'}
                          <div class="mt-2">
                            <div class="h-1.5 bg-gray-200 dark:bg-gray-700 rounded-full overflow-hidden">
                              <div
                                class="h-full {getProgressBarColor(service.status)} rounded-full transition-all duration-200"
                                style="width: {service.progressPercent}%"
                              ></div>
                            </div>
                          </div>
                        {/if}

                        <!-- Last log message for this service -->
                        {#if service.message}
                          <p class="mt-1 text-xs text-gray-500 dark:text-gray-400 font-mono truncate" title={service.message}>
                            {service.message}
                          </p>
                        {/if}
                      </div>

                      <!-- Progress Percentage -->
                      <div class="shrink-0 text-sm font-medium {getStatusColor(service.status)}">
                        {service.progressPercent}%
                      </div>
                    </div>
                  </div>
                {/each}
              {:else}
                <!-- Fallback: show selected services as waiting -->
                {#each Array.from(selectedServices) as serviceName (serviceName)}
                  <div class="p-3 rounded-lg border border-gray-200 dark:border-gray-700 bg-gray-50/50 dark:bg-gray-800/50">
                    <div class="flex items-center gap-3">
                      <Loader2 class="w-5 h-5 text-gray-400 animate-spin" />
                      <span class="font-medium text-gray-900 dark:text-white">{serviceName}</span>
                      <Badge class="bg-gray-100 dark:bg-gray-700 text-gray-500 dark:text-gray-400 text-xs">
                        {$t('update.progress.waiting')}
                      </Badge>
                    </div>
                  </div>
                {/each}
              {/if}
            </div>

            <!-- Logs Section (Expandable) -->
            {#if updateLogs.length > 0}
              <div class="mt-4 border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden">
                <button
                  class="w-full px-4 py-2 flex items-center justify-between bg-gray-100 dark:bg-gray-700/50 hover:bg-gray-150 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                  onclick={() => logsExpanded = !logsExpanded}
                >
                  <span class="text-sm font-medium text-gray-700 dark:text-gray-300">
                    {$t('update.logs')} ({updateLogs.length})
                  </span>
                  {#if logsExpanded}
                    <ChevronUp class="w-4 h-4 text-gray-500" />
                  {:else}
                    <ChevronDown class="w-4 h-4 text-gray-500" />
                  {/if}
                </button>

                {#if logsExpanded}
                  <div
                    bind:this={logsContainer}
                    class="max-h-48 overflow-y-auto p-3 bg-gray-900 text-gray-100 font-mono text-xs"
                  >
                    {#each updateLogs as log, i (i)}
                      <div class="whitespace-pre-wrap break-all">{log}</div>
                    {/each}
                  </div>
                {/if}
              </div>
            {/if}
          </div>
        {:else if updateCheck.images.length === 0}
          <div class="text-center py-8 text-gray-500 dark:text-gray-400">
            <Package class="w-12 h-12 mx-auto mb-3 opacity-50" />
            <p>{$t('update.noImages')}</p>
          </div>
        {:else}
          <!-- Select All / Deselect All -->
          {#if updatableServices.length > 1}
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

          <!-- Services List -->
          <div class="space-y-3">
            {#each updateCheck.images as image (image.serviceName)}
              {@const isSelected = selectedServices.has(image.serviceName)}
              {@const canUpdate = image.updateAvailable && image.updatePolicy !== 'disabled'}

              <div
                class="p-4 rounded-lg border transition-all {canUpdate
                  ? (isSelected
                    ? 'border-blue-300 dark:border-blue-600 bg-blue-50/50 dark:bg-blue-900/20'
                    : 'border-gray-200 dark:border-gray-700 hover:border-gray-300 dark:hover:border-gray-600 bg-white dark:bg-gray-800/50')
                  : 'border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800/30 opacity-60'}"
              >
                <div class="flex items-start gap-3">
                  <!-- Checkbox -->
                  {#if canUpdate}
                    <Checkbox
                      checked={isSelected}
                      onclick={() => toggleService(image.serviceName)}
                      class="mt-1"
                    />
                  {:else}
                    <div class="w-4 h-4 mt-1"></div>
                  {/if}

                  <!-- Service Info -->
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium text-gray-900 dark:text-white">
                        {image.serviceName}
                      </span>

                      <!-- Status Badges -->
                      {#if image.updateAvailable}
                        <Badge class="bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300 text-xs">
                          <Download class="w-3 h-3 mr-1" />
                          {$t('update.updateAvailable')}
                        </Badge>
                      {:else if image.isLocalBuild}
                        <Badge class="bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300 text-xs">
                          <HardDrive class="w-3 h-3 mr-1" />
                          {$t('update.localBuild')}
                        </Badge>
                      {:else if image.isPinnedDigest}
                        <Badge class="bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300 text-xs">
                          <Pin class="w-3 h-3 mr-1" />
                          {$t('update.pinnedDigest')}
                        </Badge>
                      {:else}
                        <Badge class="bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-300 text-xs">
                          <CheckCircle2 class="w-3 h-3 mr-1" />
                          {$t('update.upToDate')}
                        </Badge>
                      {/if}

                      {#if image.updatePolicy === 'disabled'}
                        <Badge class="bg-gray-100 text-gray-600 dark:bg-gray-700 dark:text-gray-400 text-xs">
                          {$t('update.disabled')}
                        </Badge>
                      {/if}
                    </div>

                    <!-- Image Name -->
                    <p class="text-sm text-gray-600 dark:text-gray-400 mt-1 font-mono truncate" title={image.image}>
                      {image.image}
                    </p>

                    <!-- Digest Comparison -->
                    {#if image.updateAvailable && image.localDigest && image.remoteDigest}
                      <div class="mt-2 text-xs space-y-1.5">
                        <!-- Local Digest Row -->
                        <div class="flex items-center gap-2 flex-wrap">
                          <span class="text-gray-500 dark:text-gray-400 w-14 shrink-0">{$t('update.local')}:</span>
                          <code class="px-1.5 py-0.5 bg-gray-100 dark:bg-gray-700 rounded text-gray-700 dark:text-gray-300" title={image.localDigest}>
                            {truncateDigest(image.localDigest)}
                          </code>
                          <button
                            class="p-0.5 rounded hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors cursor-pointer"
                            onclick={() => copyToClipboard(image.localDigest!, `local-${image.serviceName}`)}
                            title="Copy digest"
                          >
                            {#if copiedDigests.has(`local-${image.serviceName}`)}
                              <Check class="w-3 h-3 text-green-600 dark:text-green-400" />
                            {:else}
                              <Copy class="w-3 h-3 text-gray-500 dark:text-gray-400" />
                            {/if}
                          </button>
                          {#if image.localCreatedAt}
                            <span class="text-gray-400 dark:text-gray-500">
                              {formatDate(image.localCreatedAt)}
                            </span>
                          {/if}
                        </div>
                        <!-- Remote Digest Row -->
                        <div class="flex items-center gap-2 flex-wrap">
                          <span class="text-gray-500 dark:text-gray-400 w-14 shrink-0">{$t('update.remote')}:</span>
                          <code class="px-1.5 py-0.5 bg-blue-100 dark:bg-blue-900/30 rounded text-blue-700 dark:text-blue-300" title={image.remoteDigest}>
                            {truncateDigest(image.remoteDigest)}
                          </code>
                          <button
                            class="p-0.5 rounded hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors cursor-pointer"
                            onclick={() => copyToClipboard(image.remoteDigest!, `remote-${image.serviceName}`)}
                            title="Copy digest"
                          >
                            {#if copiedDigests.has(`remote-${image.serviceName}`)}
                              <Check class="w-3 h-3 text-green-600 dark:text-green-400" />
                            {:else}
                              <Copy class="w-3 h-3 text-gray-500 dark:text-gray-400" />
                            {/if}
                          </button>
                          {#if image.remoteCreatedAt}
                            <span class="text-blue-500 dark:text-blue-400">
                              {formatDate(image.remoteCreatedAt)}
                            </span>
                          {/if}
                        </div>
                      </div>
                    {/if}

                    <!-- Error -->
                    {#if image.error}
                      <div class="mt-2 flex items-center gap-1.5 text-xs text-red-600 dark:text-red-400">
                        <AlertCircle class="w-3.5 h-3.5" />
                        <span>{image.error}</span>
                      </div>
                    {/if}
                  </div>
                </div>
              </div>
            {/each}
          </div>
        {/if}
      </div>

      <!-- Footer -->
      <div class="px-6 py-4 border-t border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800/50 flex items-center justify-between">
        {#if isUpdating}
          <p class="text-sm text-gray-500 dark:text-gray-400">
            {$t('update.pleaseWait')}
          </p>
          <div class="flex items-center gap-2 text-sm text-blue-600 dark:text-blue-400">
            <Loader2 class="w-4 h-4 animate-spin" />
            {updateProgress?.overallProgress ?? 0}%
          </div>
        {:else}
          <div class="flex flex-col gap-3 w-full">
            <div class="flex items-center justify-between">
              <p class="text-sm text-gray-500 dark:text-gray-400">
                {selectedServices.size} {$t('update.servicesSelected')}
              </p>
              <div class="flex gap-3">
                <button
                  class="px-4 py-2 text-sm font-medium rounded-lg bg-gray-200 dark:bg-gray-700 text-gray-700 dark:text-gray-300 hover:bg-gray-300 dark:hover:bg-gray-600 transition-colors cursor-pointer"
                  onclick={onClose}
                >
                  {$t('common.cancel')}
                </button>
                <button
                  class="px-4 py-2 text-sm font-medium rounded-lg bg-blue-600 text-white hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
                  onclick={handleUpdate}
                  disabled={noneSelected || updateMutation.isPending}
                >
                  {#if updateMutation.isPending}
                    <Loader2 class="w-4 h-4 animate-spin" />
                  {:else}
                    <Download class="w-4 h-4" />
                  {/if}
                  {$t('update.updateSelected')} ({selectedServices.size})
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
          </div>
        {/if}
      </div>
    </div>
  </div>
{/if}
