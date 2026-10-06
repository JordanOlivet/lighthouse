<script lang="ts">
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { Container, Play, Square, RotateCw, Trash2, Search, Download, Loader2, MoreHorizontal, ChevronRight } from 'lucide-svelte';
  import { containersApi } from '$lib/api';
  import { updateApi } from '$lib/api/update';
  import LoadingState from '$lib/components/common/LoadingState.svelte';
  import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
  import ContainerUpdateDialog from '$lib/components/update/ContainerUpdateDialog.svelte';
  import BulkContainerUpdateDialog from '$lib/components/update/BulkContainerUpdateDialog.svelte';
  import ActionButton from '$lib/components/common/ActionButton.svelte';
  import StateBadge from '$lib/components/common/StateBadge.svelte';
  import CrashLoopBadge from '$lib/components/common/CrashLoopBadge.svelte';
  import DraggableTableHeader from '$lib/components/common/DraggableTableHeader.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Input from '$lib/components/ui/input.svelte';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { goto } from '$app/navigation';
  import { EntityState } from '$lib/types';
  import type { ColumnDefinition } from '$lib/types/table';
  import { isAdmin } from '$lib/stores/auth.svelte';
  import { createColumnPreferences } from '$lib/stores/columnPreferences.svelte';
  import { containerHasUpdate, setContainerUpdateResult, handleContainerUpdatesCheckedEvent, hasAnyContainerUpdates, containersWithUpdatesCount, reconcileContainerUpdateState } from '$lib/stores/containerUpdate.svelte';
  import { syncFromContainers } from '$lib/stores/crashLoop.svelte';
  import type { ContainerUpdateCheckResponse, ContainerUpdatesCheckedEvent } from '$lib/types/update';
  import { compareIpAddress, comparePorts } from '$lib/utils/sortUtils';
  import ActionStatusBadge from '$lib/components/common/ActionStatusBadge.svelte';
  import { syncBadgesFromContainers } from '$lib/stores/actionLog.svelte';

  // Column definitions for containers table
  const containerColumns: ColumnDefinition[] = [
    { id: 'name', labelKey: 'containers.name', sortKey: 'name' },
    { id: 'image', labelKey: 'containers.image', sortKey: 'image' },
    { id: 'ipAddress', labelKey: 'containers.ipAddress', sortKey: 'ipAddress' },
    { id: 'ports', labelKey: 'containers.ports', sortKey: 'ports' },
    { id: 'state', labelKey: 'containers.state', sortKey: 'state' },
    { id: 'status', labelKey: 'containers.status', sortKey: 'status' },
    { id: 'actions', labelKey: 'containers.actions' }
  ];

  const defaultColumnOrder = containerColumns.map(c => c.id);
  const columnPrefs = createColumnPreferences('containers', defaultColumnOrder);

  // Grouped filter state
  type SortKey = 'name' | 'image' | 'ipAddress' | 'ports' | 'state' | 'status';
  type SortDir = 'asc' | 'desc';

  let filters = $state({
    search: '',
    sortKey: 'name' as SortKey,
    sortDir: 'asc' as SortDir
  });

  // Dialog state
  let confirmDialog = $state({
    open: false,
    containerId: '',
    containerName: '',
    isRunning: false
  });

  // Update dialog state
  let updateDialogOpen = $state(false);
  let containerUpdateCheck = $state<ContainerUpdateCheckResponse | null>(null);
  let checkingUpdateFor = $state<string | null>(null);
  let bulkUpdateDialogOpen = $state(false);
  let mobileActionContainer = $state<string | null>(null);

  const queryClient = useQueryClient();

  // SSE is now handled globally in the protected layout
  // The SSE-Query bridge automatically invalidates queries on events
  const containersQuery = createQuery(() => ({
    queryKey: ['containers'],
    queryFn: () => containersApi.list(),
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    staleTime: 0,
  }));

  // Reconcile container update state when the container list changes
  // This removes stale entries for containers that were destroyed/recreated
  $effect(() => {
    const data = containersQuery.data;
    if (data && data.length > 0) {
      reconcileContainerUpdateState(new Set(data.map((c: any) => c.id)));
    }
  });

  // Sync crash loop state from API data
  $effect(() => {
    if (containersQuery.data) {
      syncFromContainers(containersQuery.data);
      syncBadgesFromContainers(containersQuery.data);
    }
  });

  // Container Mutations
  // Note: The SSE-Query bridge handles cache invalidation automatically
  const startMutation = createMutation(() => ({
    mutationFn: (id: string) => containersApi.start(id),
    onSuccess: () => toast.success($t('containers.startSuccess')),
    onError: (error: any) => {
      toast.error(error.response?.data?.message || $t('containers.startFailed'));
    },
  }));

  const stopMutation = createMutation(() => ({
    mutationFn: (id: string) => containersApi.stop(id),
    onSuccess: () => toast.success($t('containers.stopSuccess')),
    onError: (error: any) => {
      toast.error(error.response?.data?.message || $t('containers.stopFailed'));
    },
  }));

  const restartMutation = createMutation(() => ({
    mutationFn: (id: string) => containersApi.restart(id),
    onSuccess: () => toast.success($t('containers.restartSuccess')),
    onError: (error: any) => {
      toast.error(error.response?.data?.message || $t('containers.restartFailed'));
    },
  }));

  const removeMutation = createMutation(() => ({
    mutationFn: ({ id, force }: { id: string; force: boolean }) => containersApi.remove(id, force),
    onSuccess: () => {
      toast.success($t('containers.removeSuccess'));
      confirmDialog = { open: false, containerId: '', containerName: '', isRunning: false };
    },
    onError: (error: any) => {
      toast.error(error.response?.data?.message || $t('containers.removeFailed'));
    },
  }));

  // Check update mutation
  const checkUpdateMutation = createMutation(() => ({
    mutationFn: (containerId: string) => updateApi.checkContainerUpdate(containerId, true),
    onSuccess: (data: ContainerUpdateCheckResponse) => {
      containerUpdateCheck = data;
      setContainerUpdateResult(data.containerId, data.updateAvailable);
      updateDialogOpen = true;
      checkingUpdateFor = null;
    },
    onError: (error: Error) => {
      toast.error($t('update.checkFailed') + ': ' + error.message);
      checkingUpdateFor = null;
    },
  }));

  // Check all container updates mutation
  const checkAllUpdatesMutation = createMutation(() => ({
    mutationFn: () => updateApi.checkAllContainerUpdates(true),
    onSuccess: (data: ContainerUpdatesCheckedEvent) => {
      handleContainerUpdatesCheckedEvent(data);
      toast.success($t('update.checkForUpdates') + ' - OK');
    },
    onError: (error: Error) => {
      toast.error($t('update.checkFailed') + ': ' + error.message);
    },
  }));

  function handleCheckUpdate(containerId: string) {
    checkingUpdateFor = containerId;
    checkUpdateMutation.mutate(containerId);
  }

  function closeUpdateDialog() {
    updateDialogOpen = false;
    containerUpdateCheck = null;
  }

  const filteredAndSortedContainers = $derived.by(() => {
    // First filter
    const filtered = (containersQuery.data ?? []).filter((c: any) =>
      c.name.toLowerCase().includes(filters.search.toLowerCase()) ||
      c.image.toLowerCase().includes(filters.search.toLowerCase())
    );

    // Then sort
    return [...filtered].sort((a: any, b: any) => {
      // Handle IP and Ports with dedicated comparison functions
      if (filters.sortKey === 'ipAddress') {
        return compareIpAddress(a.ipAddress, b.ipAddress, filters.sortDir);
      }
      if (filters.sortKey === 'ports') {
        return comparePorts(a.ports, b.ports, filters.sortDir);
      }

      const getVal = (c: any) => {
        switch (filters.sortKey) {
          case 'name':
            return c.name.startsWith('/') ? c.name.slice(1) : c.name;
          case 'image':
            return c.image || '';
          case 'state':
            return c.state || '';
          case 'status':
            return c.status || '';
          default:
            return '';
        }
      };
      const va = getVal(a)?.toString().toLowerCase();
      const vb = getVal(b)?.toString().toLowerCase();
      if (va < vb) return filters.sortDir === 'asc' ? -1 : 1;
      if (va > vb) return filters.sortDir === 'asc' ? 1 : -1;
      return 0;
    });
  });

  function toggleSort(key: string) {
    if (filters.sortKey === key) {
      filters.sortDir = filters.sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      filters.sortKey = key as SortKey;
      filters.sortDir = 'asc';
    }
  }

  function handleColumnReorder(fromIndex: number, toIndex: number) {
    columnPrefs.moveColumn(fromIndex, toIndex);
  }

  function toggleMobileActions(containerId: string) {
    mobileActionContainer = mobileActionContainer === containerId ? null : containerId;
  }

  function closeMobileActionsOnOutsideClick(event: MouseEvent) {
    const target = event.target as HTMLElement;
    if (!target.closest('[data-mobile-container-actions]')) {
      mobileActionContainer = null;
    }
  }

  function displayName(name: string) {
    return name.startsWith('/') ? name.slice(1) : name;
  }

  function handleRemove() {
    removeMutation.mutate({
      id: confirmDialog.containerId,
      force: confirmDialog.isRunning
    });
  }
</script>

<svelte:window
  onclick={closeMobileActionsOnOutsideClick}
  onkeydown={(event) => event.key === 'Escape' && (mobileActionContainer = null)}
/>

<div class="space-y-4">
  {#if containersQuery.isLoading}
    <LoadingState message={$t('common.loading')} />
  {:else if containersQuery.error}
    <div class="text-center py-8 text-red-500">
      {$t('errors.failedToLoad')}: {containersQuery.error.message}
    </div>
  {:else}
    <!-- Page Header -->
    <div class="mb-2">
      <div class="flex flex-col items-start justify-between gap-3 sm:flex-row sm:items-center">
        <div class="min-w-0">
          <h1 class="text-2xl font-bold text-gray-900 dark:text-white mb-1">{$t('containers.title')}</h1>
          <p class="text-base text-gray-600 dark:text-gray-400">
            {$t('containers.subtitle')}
          </p>
        </div>
        {#if isAdmin.current}
          <!-- Desktop actions; on mobile they sit next to the search bar instead. -->
          <div class="hidden items-center gap-2 sm:flex sm:flex-wrap sm:justify-end">
            <button
              onclick={() => checkAllUpdatesMutation.mutate()}
              disabled={checkAllUpdatesMutation.isPending}
              class="flex items-center justify-center gap-2 px-3 py-1 text-xs font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-800 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {#if checkAllUpdatesMutation.isPending}
                <Loader2 class="w-3 h-3 animate-spin" />
              {:else}
                <Download class="w-3 h-3" />
              {/if}
              {$t('update.checkContainerUpdates')}
            </button>
            {#if hasAnyContainerUpdates.current}
              <button
                onclick={() => bulkUpdateDialogOpen = true}
                class="flex items-center justify-center gap-2 px-3 py-1 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg transition-colors cursor-pointer"
              >
                <Download class="w-3 h-3" />
                {$t('update.updateAll')} ({containersWithUpdatesCount.current})
              </button>
            {/if}
          </div>
        {/if}
      </div>
    </div>

    <!-- Search Bar (+ icon action on mobile) -->
    <div class="flex items-center gap-2">
      <div class="relative min-w-0 flex-1">
        <Search class="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-gray-400" />
        <Input
          type="text"
          placeholder={$t('common.search')}
          bind:value={filters.search}
          onkeydown={(e) => e.key === 'Escape' && (filters.search = '')}
          class="pl-10"
        />
      </div>
      {#if isAdmin.current}
        <button
          onclick={() => checkAllUpdatesMutation.mutate()}
          disabled={checkAllUpdatesMutation.isPending}
          class="flex h-10 w-10 shrink-0 items-center justify-center rounded-md border border-gray-300 bg-white text-gray-700 transition-colors hover:bg-gray-50 disabled:opacity-50 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700 sm:hidden"
          title={$t('update.checkContainerUpdates')}
          aria-label={$t('update.checkContainerUpdates')}
        >
          {#if checkAllUpdatesMutation.isPending}
            <Loader2 class="h-4 w-4 animate-spin" />
          {:else}
            <Download class="h-4 w-4" />
          {/if}
        </button>
      {/if}
    </div>

    <!-- Mobile: bulk update surfaces as a banner only when there is something to update. -->
    {#if isAdmin.current && hasAnyContainerUpdates.current}
      <button
        onclick={() => bulkUpdateDialogOpen = true}
        class="flex min-h-11 w-full items-center gap-3 rounded-lg border border-blue-200 bg-blue-50 px-3 text-left text-sm text-blue-800 transition-colors hover:bg-blue-100 dark:border-blue-900 dark:bg-blue-950/40 dark:text-blue-200 dark:hover:bg-blue-950/70 sm:hidden"
      >
        <Download class="h-4 w-4 shrink-0" />
        <span class="min-w-0 flex-1 truncate">
          <span class="font-semibold">{containersWithUpdatesCount.current}</span> {$t('update.updatesAvailable')}
        </span>
        <span class="flex shrink-0 items-center gap-0.5 font-semibold">
          {$t('update.updateAll')}
          <ChevronRight class="h-4 w-4" />
        </span>
      </button>
    {/if}

    {#if !containersQuery.data || containersQuery.data.length === 0}
      <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
        <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
          <Container class="w-8 h-8 text-gray-400" />
        </div>
        <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
          {$t('containers.noContainers')}
        </h3>
        <p class="text-sm text-gray-600 dark:text-gray-400">
          {$t('containers.subtitle')}
        </p>
      </div>
    {:else if filteredAndSortedContainers.length === 0}
      <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
        <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
          <Search class="w-8 h-8 text-gray-400" />
        </div>
        <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
          No containers found
        </h3>
        <p class="text-sm text-gray-600 dark:text-gray-400">
          Try adjusting your search criteria
        </p>
      </div>
    {:else}
      <!-- Compact mobile list: one shared surface instead of repeated cards. -->
      <div class="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm dark:border-gray-700 dark:bg-gray-800 md:hidden">
        <div class="grid min-h-9 grid-cols-[minmax(0,1fr)_5.75rem_2.5rem] items-center gap-1 border-b border-gray-200 bg-gray-50 px-3 text-[10px] font-semibold uppercase tracking-wide text-gray-500 dark:border-gray-700 dark:bg-gray-900/60 dark:text-gray-400">
          <span>{$t('containers.name')}</span>
          <span>{$t('containers.state')}</span>
          <span class="sr-only">{$t('containers.actions')}</span>
        </div>

        {#each filteredAndSortedContainers as container (container.id)}
          {@const isRunning = container.state === EntityState.Running}
          {@const actionsOpen = mobileActionContainer === container.id}
          <article
            data-mobile-container-actions
            class="border-b border-gray-200 last:border-b-0 dark:border-gray-700 {actionsOpen ? 'bg-blue-50 dark:bg-blue-950/30' : ''}"
          >
            <div
              class="grid min-h-16 grid-cols-[minmax(0,1fr)_5.75rem_2.5rem] items-center gap-1 px-3 py-2 transition-colors"
              class:border-l-2={actionsOpen}
              class:border-blue-500={actionsOpen}
              class:pl-2.5={actionsOpen}
            >
              <div class="min-w-0 pr-1">
                <div class="flex min-w-0 items-center gap-1.5">
                  <button
                    class="min-w-0 truncate text-left text-sm font-semibold text-blue-600 hover:underline focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-blue-400"
                    onclick={() => goto(`/containers/${container.id}`)}
                    title={$t('containers.viewDetails')}
                  >
                    {displayName(container.name)}
                  </button>
                  <ActionStatusBadge entityType="container" entityId={container.id} />
                </div>
                <p class="mt-0.5 truncate text-[11px] text-gray-600 dark:text-gray-300" title={container.image}>
                  {container.image}
                </p>
                <p class="truncate text-[10px] text-gray-500 dark:text-gray-400" title={container.status}>
                  {container.status}
                </p>
              </div>

              <div class="flex min-w-0 flex-col items-start gap-1 overflow-hidden">
                <StateBadge status={container.state} size="sm" class="max-w-full whitespace-nowrap text-[10px]" />
                <CrashLoopBadge entityType="container" entityId={container.id} />
              </div>

              <button
                class="relative flex h-10 w-10 items-center justify-center rounded-lg text-gray-500 transition-colors hover:bg-gray-100 hover:text-gray-900 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-white {actionsOpen ? 'bg-gray-100 dark:bg-gray-700' : ''}"
                aria-label="{$t('containers.actions')} · {displayName(container.name)}"
                aria-controls="mobile-container-actions-{container.id}"
                aria-expanded={actionsOpen}
                onclick={(event) => {
                  event.stopPropagation();
                  toggleMobileActions(container.id);
                }}
              >
                <MoreHorizontal class="h-5 w-5" />
                {#if containerHasUpdate(container.id)}
                  <span class="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-red-500"></span>
                {/if}
              </button>
            </div>

            {#if actionsOpen}
              <div
                id="mobile-container-actions-{container.id}"
                class="space-y-2.5 border-t border-gray-200 bg-gray-50 p-2.5 dark:border-gray-700 dark:bg-gray-900/70"
              >
                <dl class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 px-1 text-[11px]">
                  <dt class="text-gray-500 dark:text-gray-400">{$t('containers.ipAddress')}</dt>
                  <dd class="truncate font-mono text-gray-700 dark:text-gray-300">{container.ipAddress || '-'}</dd>
                  <dt class="text-gray-500 dark:text-gray-400">{$t('containers.ports')}</dt>
                  <dd class="font-mono text-gray-700 dark:text-gray-300">
                    {#if container.ports && container.ports.length > 0}
                      {#each container.ports as port}
                        <div class="truncate">{port}</div>
                      {/each}
                    {:else}
                      -
                    {/if}
                  </dd>
                  <dt class="text-gray-500 dark:text-gray-400">ID</dt>
                  <dd class="font-mono text-gray-700 dark:text-gray-300">{container.id.substring(0, 12)}</dd>
                </dl>

                <div class="grid grid-cols-2 gap-2">
                  {#if isAdmin.current}
                    <button
                      class="relative flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-gray-700 transition-colors hover:bg-gray-100 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700"
                      disabled={checkingUpdateFor === container.id}
                      onclick={() => handleCheckUpdate(container.id)}
                    >
                      {#if checkingUpdateFor === container.id}
                        <Loader2 class="h-4 w-4 animate-spin text-blue-500" />
                      {:else}
                        <Download class="h-4 w-4 text-blue-500" />
                      {/if}
                      {$t('update.checkUpdates')}
                      {#if containerHasUpdate(container.id)}
                        <span class="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-red-500"></span>
                      {/if}
                    </button>
                  {/if}

                  {#if isRunning}
                    <button
                      class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-blue-600 transition-colors hover:bg-gray-100 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-blue-400 dark:hover:bg-gray-700"
                      disabled={restartMutation.isPending}
                      onclick={() => restartMutation.mutate(container.id)}
                    >
                      <RotateCw class="h-4 w-4" />
                      {$t('containers.restart')}
                    </button>
                    <button
                      class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-yellow-600 transition-colors hover:bg-gray-100 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-yellow-400 dark:hover:bg-gray-700"
                      disabled={stopMutation.isPending}
                      onclick={() => stopMutation.mutate(container.id)}
                    >
                      <Square class="h-4 w-4" />
                      {$t('containers.stop')}
                    </button>
                  {:else}
                    <button
                      class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-green-600 transition-colors hover:bg-gray-100 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-green-400 dark:hover:bg-gray-700"
                      disabled={startMutation.isPending}
                      onclick={() => startMutation.mutate(container.id)}
                    >
                      <Play class="h-4 w-4" />
                      {$t('containers.start')}
                    </button>
                  {/if}

                  <button
                    class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-red-400 dark:hover:bg-red-950/30"
                    disabled={removeMutation.isPending}
                    onclick={() => confirmDialog = { open: true, containerId: container.id, containerName: container.name, isRunning }}
                  >
                    <Trash2 class="h-4 w-4" />
                    {$t('containers.remove')}
                  </button>
                </div>
              </div>
            {/if}
          </article>
        {/each}
      </div>

      <div class="hidden bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-xl border border-gray-200 dark:border-gray-700 overflow-visible shadow hover:shadow-lg transition-all duration-300 md:block">
        <div class="overflow-x-auto">
          <table class="w-full">
            <DraggableTableHeader
              columns={containerColumns}
              columnOrder={columnPrefs.order}
              sortKey={filters.sortKey}
              sortDir={filters.sortDir}
              onSort={toggleSort}
              onReorder={handleColumnReorder}
            />
            <tbody class="divide-y divide-gray-100 dark:divide-gray-700">
              {#each filteredAndSortedContainers as container (container.id)}
                {@const isRunning = container.state === EntityState.Running}
                <tr class="hover:bg-white dark:hover:bg-gray-800 transition-all">
                  {#each columnPrefs.order as colId (colId)}
                    {#if colId === 'name'}
                      <td class="px-4 py-2 whitespace-nowrap">
                        <div class="flex items-center gap-1">
                          <button
                            class="text-sm font-medium text-blue-600 dark:text-blue-400 hover:underline focus:outline-none cursor-pointer"
                            onclick={() => goto(`/containers/${container.id}`)}
                            title={$t('containers.viewDetails')}
                          >
                            {container.name.startsWith('/') ? container.name.slice(1) : container.name}
                          </button>
                          <ActionStatusBadge entityType="container" entityId={container.id} />
                        </div>
                        <div class="text-[10px] text-gray-500 dark:text-gray-400 font-mono">
                          {container.id.substring(0, 12)}
                        </div>
                      </td>
                    {:else if colId === 'image'}
                      <td class="px-4 py-2">
                        <div class="text-xs text-gray-900 dark:text-gray-300">
                          {container.image}
                        </div>
                      </td>
                    {:else if colId === 'ipAddress'}
                      <td class="px-4 py-2">
                        <div class="text-xs text-gray-500 dark:text-gray-400 font-mono">
                          {container.ipAddress || '-'}
                        </div>
                      </td>
                    {:else if colId === 'ports'}
                      <td class="px-4 py-2">
                        <div class="text-xs text-gray-500 dark:text-gray-400 font-mono">
                          {#if container.ports && container.ports.length > 0}
                            {#each container.ports as port}
                              <div>{port}</div>
                            {/each}
                          {:else}
                            -
                          {/if}
                        </div>
                      </td>
                    {:else if colId === 'state'}
                      <td class="px-4 py-2 whitespace-nowrap">
                        <div class="flex items-center gap-1.5">
                          <StateBadge status={container.state} size="sm" />
                          <CrashLoopBadge entityType="container" entityId={container.id} />
                        </div>
                      </td>
                    {:else if colId === 'status'}
                      <td class="px-4 py-2">
                        <div class="text-xs text-gray-500 dark:text-gray-400">
                          {container.status}
                        </div>
                      </td>
                    {:else if colId === 'actions'}
                      <td class="px-4 py-2 whitespace-nowrap text-xs">
                        <div class="flex items-center gap-1">
                          {#if isAdmin.current}
                            <div class="relative">
                              <ActionButton
                                icon={checkingUpdateFor === container.id ? Loader2 : Download}
                                variant="update"
                                title={$t('update.checkUpdates')}
                                disabled={checkingUpdateFor === container.id}
                                class={checkingUpdateFor === container.id ? 'animate-spin' : ''}
                                onclick={() => handleCheckUpdate(container.id)}
                              />
                              {#if containerHasUpdate(container.id)}
                                <span class="absolute -top-0.5 -right-0.5 w-2 h-2 bg-red-500 rounded-full"></span>
                              {/if}
                            </div>
                          {/if}
                          {#if isRunning}
                            <ActionButton
                              icon={RotateCw}
                              variant="restart"
                              title={$t('containers.restart')}
                              disabled={restartMutation.isPending}
                              onclick={() => restartMutation.mutate(container.id)}
                            />
                            <ActionButton
                              icon={Square}
                              variant="stop"
                              title={$t('containers.stop')}
                              disabled={stopMutation.isPending}
                              onclick={() => stopMutation.mutate(container.id)}
                            />
                          {:else}
                            <ActionButton
                              icon={Play}
                              variant="play"
                              title={$t('containers.start')}
                              disabled={startMutation.isPending}
                              onclick={() => startMutation.mutate(container.id)}
                            />
                          {/if}
                          <ActionButton
                            icon={Trash2}
                            variant="remove"
                            title={$t('containers.remove')}
                            disabled={removeMutation.isPending}
                            onclick={() => confirmDialog = { open: true, containerId: container.id, containerName: container.name, isRunning }}
                          />
                        </div>
                      </td>
                    {/if}
                  {/each}
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </div>
    {/if}
  {/if}

  <!-- Confirm Dialog -->
  <ConfirmDialog
    open={confirmDialog.open}
    title={$t('containers.confirmRemove')}
    description={confirmDialog.isRunning
      ? $t('containers.confirmRemoveRunningWithName', { name: confirmDialog.containerName })
      : $t('containers.confirmRemoveWithName', { name: confirmDialog.containerName })}
    onconfirm={handleRemove}
    oncancel={() => confirmDialog.open = false}
  />

  <!-- Container Update Dialog -->
  <ContainerUpdateDialog
    open={updateDialogOpen}
    checkResult={containerUpdateCheck}
    onClose={closeUpdateDialog}
  />

  <!-- Bulk Container Update Dialog -->
  <BulkContainerUpdateDialog
    open={bulkUpdateDialogOpen}
    containers={containersQuery.data ?? []}
    onClose={() => bulkUpdateDialogOpen = false}
  />
</div>
