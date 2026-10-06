<script lang="ts">
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import {
    Play,
    Square,
    RotateCw,
    Trash2,
    Zap,
    RefreshCw,
    Search,
    ChevronRight,
    MoreHorizontal,
    Download,
    Loader2,
    RefreshCwOff
  } from 'lucide-svelte';
  import { composeApi } from '$lib/api/compose';
  import { containersApi } from '$lib/api/containers';
  import { updateApi } from '$lib/api/update';
  import type { ComposeProject, ComposeService } from '$lib/types';
  import type { ProjectUpdateCheckResponse } from '$lib/types/update';
  import type { ColumnDefinition } from '$lib/types/table';
  import { EntityState } from '$lib/types';
  import StateBadge from '$lib/components/common/StateBadge.svelte';
  import CrashLoopBadge from '$lib/components/common/CrashLoopBadge.svelte';
  import LoadingState from '$lib/components/common/LoadingState.svelte';
  import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
  import ServiceUpdateDialog from '$lib/components/update/ServiceUpdateDialog.svelte';
  import BulkUpdateDialog from '$lib/components/update/BulkUpdateDialog.svelte';
  import DraggableTableHeader from '$lib/components/common/DraggableTableHeader.svelte';
  import ActionButton from '$lib/components/common/ActionButton.svelte';
  import Input from '$lib/components/ui/input.svelte';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { goto } from '$app/navigation';
  import { isAdmin } from '$lib/stores/auth.svelte';
  import { createColumnPreferences } from '$lib/stores/columnPreferences.svelte';
  import { projectHasUpdates, hasAnyUpdates, projectsWithUpdatesCount } from '$lib/stores/projectUpdate.svelte';
  import { syncFromProjects } from '$lib/stores/crashLoop.svelte';
  import { compareIpAddress, comparePorts } from '$lib/utils/sortUtils';
  import ActionStatusBadge from '$lib/components/common/ActionStatusBadge.svelte';
  import { syncBadgesFromProjects } from '$lib/stores/actionLog.svelte';

  // Column definitions for projects table
  const projectColumns: ColumnDefinition[] = [
    { id: 'name', labelKey: 'compose.projectName', sortKey: 'name' },
    { id: 'state', labelKey: 'containers.state', sortKey: 'state' },
    { id: 'services', labelKey: 'compose.services', sortKey: 'services' },
    { id: 'actions', labelKey: 'containers.actions', width: '12rem' }
  ];

  // Column definitions for services table
  const serviceColumns: ColumnDefinition[] = [
    { id: 'name', labelKey: 'containers.name', sortKey: 'name', width: '18%' },
    { id: 'image', labelKey: 'containers.image', sortKey: 'image', width: '20%' },
    { id: 'ipAddress', labelKey: 'containers.ipAddress', sortKey: 'ipAddress', width: '10%' },
    { id: 'ports', labelKey: 'containers.ports', sortKey: 'ports', width: '10%' },
    { id: 'state', labelKey: 'containers.state', sortKey: 'state', width: '14%' },
    { id: 'status', labelKey: 'containers.status', sortKey: 'status', width: '13%' },
    { id: 'actions', labelKey: 'containers.actions', width: '9rem' }
  ];

  const defaultProjectColumnOrder = projectColumns.map(c => c.id);
  const defaultServiceColumnOrder = serviceColumns.map(c => c.id);

  const projectColumnPrefs = createColumnPreferences('compose-projects', defaultProjectColumnOrder);
  const serviceColumnPrefs = createColumnPreferences('compose-services', defaultServiceColumnOrder);

  // Sorting types
  type ProjectSortKey = 'name' | 'services' | 'state';
  type ServiceSortKey = 'name' | 'image' | 'ipAddress' | 'ports' | 'state' | 'status';
  type SortDir = 'asc' | 'desc';

  // State priority for sorting (lower = more important)
  const statePriority: Record<string, number> = {
    [EntityState.Running]: 0,
    [EntityState.Degraded]: 1,
    [EntityState.Restarting]: 2,
    [EntityState.Stopped]: 3,
    [EntityState.Exited]: 4,
    [EntityState.Down]: 5,
    [EntityState.NotStarted]: 6,
    [EntityState.Created]: 7,
    [EntityState.Unknown]: 8,
  };

  // Project open state with independent service sorting
  interface ProjectOpenState {
    isOpen: boolean;
    serviceSortKey: ServiceSortKey;
    serviceSortDir: SortDir;
  }

  let filters = $state({
    search: '',
    sortKey: 'name' as ProjectSortKey,
    sortDir: 'asc' as SortDir,
  });

  let openProjects = $state<Record<string, ProjectOpenState>>({});
  let mobileActionProject = $state<string | null>(null);
  let confirmDialog = $state<{ open: boolean; title: string; description: string; onConfirm: () => void }>({
    open: false,
    title: '',
    description: '',
    onConfirm: () => {},
  });

  // Update dialog state
  let updateDialogOpen = $state(false);
  let selectedProjectForUpdate = $state<string | null>(null);
  let projectUpdateCheck = $state<ProjectUpdateCheckResponse | null>(null);
  let checkingUpdatesFor = $state<string | null>(null);

  // Bulk update dialog state
  let bulkUpdateDialogOpen = $state(false);

  const queryClient = useQueryClient();

  // SSE is now handled globally in the protected layout
  // The SSE-Query bridge automatically invalidates queries on events
  const projectsQuery = createQuery(() => ({
    queryKey: ['compose', 'projects'],
    queryFn: () => composeApi.listProjects(),
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    staleTime: 0,
  }));

  // Sync crash loop state from API data
  $effect(() => {
    if (projectsQuery.data) {
      syncFromProjects(projectsQuery.data);
      syncBadgesFromProjects(projectsQuery.data);
    }
  });

    const projectsQueryForceRefetch = createQuery(() => ({
    queryKey: ['compose', 'projects'],
    queryFn: () => composeApi.listProjects({ refresh: true }),
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    staleTime: 0,
  }));

  // Compose Project Mutations
  // Note: The SSE-Query bridge handles cache invalidation automatically
  const upMutation = createMutation(() => ({
    mutationFn: ({ projectName, forceRecreate }: { projectName: string; forceRecreate?: boolean }) =>
      composeApi.upProject(projectName, { detach: true, forceRecreate }),
    onSuccess: () => toast.success($t('compose.upSuccess')),
    onError: () => toast.error($t('compose.failedToLoad')),
  }));

  const downMutation = createMutation(() => ({
    mutationFn: (projectName: string) => composeApi.downProject(projectName),
    onSuccess: () => toast.success($t('compose.downSuccess')),
    onError: () => toast.error($t('compose.failedToLoad')),
  }));

  const restartMutation = createMutation(() => ({
    mutationFn: (projectName: string) => composeApi.restartProject(projectName),
    onSuccess: () => toast.success($t('compose.restartSuccess')),
    onError: () => toast.error($t('compose.failedToLoad')),
  }));

  const stopMutation = createMutation(() => ({
    mutationFn: (projectName: string) => composeApi.stopProject(projectName),
    onSuccess: () => toast.success($t('compose.stopSuccess')),
    onError: () => toast.error($t('compose.failedToLoad')),
  }));

  // Container Mutations
  const startContainerMutation = createMutation(() => ({
    mutationFn: (containerId: string) => containersApi.start(containerId),
    onSuccess: () => toast.success($t('containers.startSuccess')),
    onError: () => toast.error($t('containers.failedToStart')),
  }));

  const stopContainerMutation = createMutation(() => ({
    mutationFn: (containerId: string) => containersApi.stop(containerId),
    onSuccess: () => toast.success($t('containers.stopSuccess')),
    onError: () => toast.error($t('containers.failedToStop')),
  }));

  const restartContainerMutation = createMutation(() => ({
    mutationFn: (containerId: string) => containersApi.restart(containerId),
    onSuccess: () => toast.success($t('containers.restartSuccess')),
    onError: () => toast.error($t('containers.failedToRestart')),
  }));

  const removeContainerMutation = createMutation(() => ({
    mutationFn: ({ containerId, force }: { containerId: string; force: boolean }) =>
      containersApi.remove(containerId, force),
    onSuccess: () => toast.success($t('containers.removeSuccess')),
    onError: () => toast.error($t('containers.failedToRemove')),
  }));

  // Check updates mutation
  const checkUpdatesMutation = createMutation(() => ({
    mutationFn: (projectName: string) => updateApi.checkProjectUpdates(projectName, true),
    onSuccess: (data: ProjectUpdateCheckResponse, projectName: string) => {
      projectUpdateCheck = data;
      selectedProjectForUpdate = projectName;
      updateDialogOpen = true;
      checkingUpdatesFor = null;
    },
    onError: (error: Error) => {
      toast.error($t('update.checkFailed') + ': ' + error.message);
      checkingUpdatesFor = null;
    },
  }));

  function handleCheckUpdates(projectName: string) {
    checkingUpdatesFor = projectName;
    checkUpdatesMutation.mutate(projectName);
  }

  function closeUpdateDialog() {
    updateDialogOpen = false;
    selectedProjectForUpdate = null;
    projectUpdateCheck = null;
  }

  function getProjectState(projectName: string): ProjectOpenState {
    return openProjects[projectName] ?? { isOpen: false, serviceSortKey: 'name', serviceSortDir: 'asc' };
  }

  function toggleProjectOpen(projectName: string) {
    const current = getProjectState(projectName);
    openProjects = {
      ...openProjects,
      [projectName]: { ...current, isOpen: !current.isOpen },
    };
  }

  function toggleProjectSort(key: string) {
    if (filters.sortKey === key) {
      filters.sortDir = filters.sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      filters.sortKey = key as ProjectSortKey;
      filters.sortDir = 'asc';
    }
  }

  function toggleServiceSort(projectName: string, key: string) {
    const current = getProjectState(projectName);
    if (current.serviceSortKey === key) {
      openProjects = {
        ...openProjects,
        [projectName]: { ...current, serviceSortDir: current.serviceSortDir === 'asc' ? 'desc' : 'asc' },
      };
    } else {
      openProjects = {
        ...openProjects,
        [projectName]: { ...current, serviceSortKey: key as ServiceSortKey, serviceSortDir: 'asc' },
      };
    }
  }

  function handleProjectColumnReorder(fromIndex: number, toIndex: number) {
    projectColumnPrefs.moveColumn(fromIndex, toIndex);
  }

  function handleServiceColumnReorder(fromIndex: number, toIndex: number) {
    serviceColumnPrefs.moveColumn(fromIndex, toIndex);
  }

  function getSortedServices(project: ComposeProject): ComposeService[] {
    const state = getProjectState(project.name);
    const services = [...(project.services ?? [])];

    return services.sort((a, b) => {
      // Handle IP and Ports with dedicated comparison functions
      if (state.serviceSortKey === 'ipAddress') {
        return compareIpAddress(a.ipAddress, b.ipAddress, state.serviceSortDir);
      }
      if (state.serviceSortKey === 'ports') {
        return comparePorts(a.ports, b.ports, state.serviceSortDir);
      }

      let va: string | number = '';
      let vb: string | number = '';

      switch (state.serviceSortKey) {
        case 'name':
          va = a.name?.toLowerCase() ?? '';
          vb = b.name?.toLowerCase() ?? '';
          break;
        case 'image':
          va = a.image?.toLowerCase() ?? '';
          vb = b.image?.toLowerCase() ?? '';
          break;
        case 'state':
          va = statePriority[a.state] ?? 99;
          vb = statePriority[b.state] ?? 99;
          if (va === vb) {
            va = a.name?.toLowerCase() ?? '';
            vb = b.name?.toLowerCase() ?? '';
          }
          break;
        case 'status':
          va = a.status?.toLowerCase() ?? '';
          vb = b.status?.toLowerCase() ?? '';
          break;
      }

      if (va < vb) return state.serviceSortDir === 'asc' ? -1 : 1;
      if (va > vb) return state.serviceSortDir === 'asc' ? 1 : -1;
      return 0;
    });
  }

  function navigateToProject(projectName: string) {
    goto(`/compose/projects/${encodeURIComponent(projectName)}`);
  }

  function toggleMobileActions(projectName: string) {
    mobileActionProject = mobileActionProject === projectName ? null : projectName;
  }

  function closeMobileActionsOnOutsideClick(event: MouseEvent) {
    const target = event.target as HTMLElement;
    if (!target.closest('[data-mobile-project-actions]')) {
      mobileActionProject = null;
    }
  }

  function handleRemoveProject(project: ComposeProject) {
    const isRunning = project.state === EntityState.Running;
    const message = isRunning
      ? `${$t('compose.title')} ${project.name} ${$t('containers.confirmRemoveRunning')}`
      : `${$t('common.delete')} ${$t('compose.title').toLowerCase()} ${project.name}?`;

    confirmDialog = {
      open: true,
     title: $t('common.delete'),
      description: message,
      onConfirm: () => {
        downMutation.mutate(project.name);
        confirmDialog.open = false;
      },
    };
  }

  function handleRemoveService(service: ComposeService) {
    const isRunning = service.state === EntityState.Running;
    const message = isRunning
      ? `${$t('containers.title')} ${service.name} ${$t('containers.confirmRemoveRunning')}`
      : `${$t('containers.confirmRemove')} ${service.name}?`;

    confirmDialog = {
      open: true,
     title: $t('containers.remove'),
      description: message,
      onConfirm: () => {
        removeContainerMutation.mutate({ containerId: service.id, force: isRunning });
        confirmDialog.open = false;
      },
    };
  }

  const filteredAndSortedProjects = $derived.by(() => {
    // First filter
    const filtered = (projectsQuery.data ?? []).filter((p: ComposeProject) =>
      p.name.toLowerCase().includes(filters.search.toLowerCase())
    );

    // Then sort
    return [...filtered].sort((a: ComposeProject, b: ComposeProject) => {
      let va: string | number = '';
      let vb: string | number = '';

      switch (filters.sortKey) {
        case 'name':
          va = a.name.toLowerCase();
          vb = b.name.toLowerCase();
          break;
        case 'services':
          va = a.services?.length ?? 0;
          vb = b.services?.length ?? 0;
          break;
        case 'state':
          // State priority + alphabetical secondary sort
          va = statePriority[a.state] ?? 99;
          vb = statePriority[b.state] ?? 99;
          if (va === vb) {
            // Secondary sort by name
            const nameA = a.name.toLowerCase();
            const nameB = b.name.toLowerCase();
            if (nameA < nameB) return filters.sortDir === 'asc' ? -1 : 1;
            if (nameA > nameB) return filters.sortDir === 'asc' ? 1 : -1;
            return 0;
          }
          break;
      }

      if (va < vb) return filters.sortDir === 'asc' ? -1 : 1;
      if (va > vb) return filters.sortDir === 'asc' ? 1 : -1;
      return 0;
    });
  });
</script>

<svelte:window
  onclick={closeMobileActionsOnOutsideClick}
  onkeydown={(event) => event.key === 'Escape' && (mobileActionProject = null)}
/>

<div class="space-y-4">
  <!-- Page Header -->
  <div class="mb-2">
    <div class="flex flex-col items-start justify-between gap-3 sm:flex-row sm:items-center">
      <div class="min-w-0">
        <h1 class="text-2xl font-bold text-gray-900 dark:text-white mb-1">
          {$t('compose.projects')}
        </h1>
        <p class="text-base text-gray-600 dark:text-gray-400">
          {$t('compose.subtitle')}
        </p>
      </div>
      <div class="grid w-full grid-cols-2 gap-2 sm:flex sm:w-auto sm:flex-wrap sm:justify-end">
        {#if isAdmin.current}
        <button
          onclick={() => updateApi.checkAllProjectUpdates(true)}
          class="order-1 flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 transition-colors hover:bg-gray-50 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700 sm:order-none"
        >
          <Download class="w-3 h-3" />
          {$t('update.checkForUpdates')}
        </button>
        {#if hasAnyUpdates.current}
          <button
            onclick={() => bulkUpdateDialogOpen = true}
            class="order-3 col-span-2 flex min-h-10 items-center justify-center gap-2 rounded-lg bg-blue-600 px-3 py-1 text-xs font-medium text-white transition-colors hover:bg-blue-700 sm:order-none sm:col-auto"
          >
            <Download class="w-3 h-3" />
            {$t('update.updateAll')} ({projectsWithUpdatesCount.current})
          </button>
          {/if}
        {/if}
        <button
          onclick={() => projectsQueryForceRefetch.refetch()}
          class="order-2 flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 transition-colors hover:bg-gray-50 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700 sm:order-none"
        >
          <RefreshCw class="w-3 h-3" />
          {$t('common.forceRefresh')}
        </button>
      </div>
    </div>
  </div>

  <!-- Search Bar -->
  <div class="relative">
    <Search class="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-gray-400" />
    <Input
      type="text"
      placeholder={$t('common.search')}
      bind:value={filters.search}
      onkeydown={(e) => e.key === 'Escape' && (filters.search = '')}
      class="pl-10"
    />
  </div>

  <!-- Projects List -->
  {#if projectsQuery.isLoading}
    <LoadingState message={$t('common.loading')} />
  {:else if projectsQuery.error}
    <div class="text-center py-8 text-red-500">
      {$t('compose.failedToLoad')}
    </div>
  {:else if !projectsQuery.data || projectsQuery.data.length === 0}
    <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
      <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
        <Square class="w-8 h-8 text-gray-400" />
      </div>
      <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
        {$t('compose.noProjects')}
      </h3>
      <p class="text-sm text-gray-600 dark:text-gray-400">
        {$t('compose.noProjectsMessage')}
      </p>
    </div>
  {:else if filteredAndSortedProjects.length === 0}
    <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
      <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
        <Search class="w-8 h-8 text-gray-400" />
      </div>
      <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
        {$t('compose.noProjects')}
      </h3>
      <p class="text-sm text-gray-600 dark:text-gray-400">
        {$t('common.search')}
      </p>
    </div>
  {:else}
    <!-- Compact mobile list: one shared surface instead of repeated cards. -->
    <div class="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm dark:border-gray-700 dark:bg-gray-800 md:hidden">
      <div class="grid min-h-9 grid-cols-[minmax(0,1fr)_3rem_5.75rem_2.5rem] items-center gap-1 border-b border-gray-200 bg-gray-50 px-3 text-[10px] font-semibold uppercase tracking-wide text-gray-500 dark:border-gray-700 dark:bg-gray-900/60 dark:text-gray-400">
        <span>{$t('compose.projectName')}</span>
        <span class="text-center">{$t('compose.services')}</span>
        <span>{$t('containers.state')}</span>
        <span class="sr-only">{$t('containers.actions')}</span>
      </div>

      {#each filteredAndSortedProjects as project (project.name)}
        {@const actionsOpen = mobileActionProject === project.name}
        <article
          data-mobile-project-actions
          class="border-b border-gray-200 last:border-b-0 dark:border-gray-700 {actionsOpen ? 'bg-blue-50 dark:bg-blue-950/30' : ''}"
        >
          <div
            class="grid min-h-20 grid-cols-[minmax(0,1fr)_3rem_5.75rem_2.5rem] items-center gap-1 px-3 transition-colors"
            class:border-l-2={actionsOpen}
            class:border-blue-500={actionsOpen}
            class:pl-2.5={actionsOpen}
          >
            <div class="min-w-0 pr-1">
              <div class="flex min-w-0 items-center gap-1.5">
                <button
                  class="min-w-0 truncate text-left text-sm font-semibold text-blue-600 hover:underline focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-blue-400"
                  onclick={() => navigateToProject(project.name)}
                  title={$t('compose.projectDetails')}
                >
                  {project.name}
                </button>
                <ActionStatusBadge entityType="project" entityId={project.name} />
              </div>
              {#if project.path}
                <p class="mt-0.5 truncate font-mono text-[10px] text-gray-500 dark:text-gray-400" title={project.path}>
                  {project.path}
                </p>
              {/if}
              {#if project.warning}
                <p class="mt-1 truncate text-[10px] text-amber-600 dark:text-amber-400" title={project.warning}>
                  {project.warning}
                </p>
              {/if}
            </div>

            <span class="text-center text-xs font-semibold text-gray-700 dark:text-gray-300">
              {project.services?.length ?? 0}
            </span>

            <div class="flex min-w-0 flex-col items-start gap-1 overflow-hidden">
              <StateBadge status={project.state} size="sm" class="max-w-full whitespace-nowrap text-[10px]" />
              <div class="flex items-center gap-1">
                <CrashLoopBadge entityType="project" entityId={project.name} />
                {#if project.autoUpdateEnabled === false}
                  <RefreshCwOff
                    class="h-3.5 w-3.5 shrink-0 text-gray-400"
                    aria-label={$t('compose.autoUpdateDisabled')}
                  />
                {/if}
              </div>
            </div>

            <button
              class="relative flex h-10 w-10 items-center justify-center rounded-lg text-gray-500 transition-colors hover:bg-gray-100 hover:text-gray-900 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-white {actionsOpen ? 'bg-gray-100 dark:bg-gray-700' : ''}"
              aria-label="{$t('containers.actions')} · {project.name}"
              aria-controls="mobile-project-actions-{project.name}"
              aria-expanded={actionsOpen}
              onclick={(event) => {
                event.stopPropagation();
                toggleMobileActions(project.name);
              }}
            >
              <MoreHorizontal class="h-5 w-5" />
              {#if projectHasUpdates(project.name) || (project.servicesWithUpdates != null && project.servicesWithUpdates > 0)}
                <span class="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-red-500"></span>
              {/if}
            </button>
          </div>

          {#if actionsOpen}
            <div
              id="mobile-project-actions-{project.name}"
              class="grid grid-cols-2 gap-2 border-t border-gray-200 bg-gray-50 p-2.5 dark:border-gray-700 dark:bg-gray-900/70"
            >
              {#if isAdmin.current && project.hasComposeFile}
                <button
                  class="relative flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-gray-700 transition-colors hover:bg-gray-100 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700"
                  disabled={checkingUpdatesFor === project.name}
                  onclick={() => handleCheckUpdates(project.name)}
                >
                  {#if checkingUpdatesFor === project.name}
                    <Loader2 class="h-4 w-4 animate-spin text-blue-500" />
                  {:else}
                    <Download class="h-4 w-4 text-blue-500" />
                  {/if}
                  {$t('update.checkUpdates')}
                </button>
              {/if}

              {#if project.state === EntityState.Down || project.state === EntityState.Stopped || project.state === EntityState.Exited || project.state === EntityState.Degraded || project.state === EntityState.Restarting || project.state === EntityState.Created || project.state === EntityState.NotStarted}
                {#if project.availableActions?.up}
                  <button
                    class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-green-600 transition-colors hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-800 dark:text-green-400 dark:hover:bg-gray-700"
                    onclick={() => upMutation.mutate({ projectName: project.name })}
                  >
                    <Play class="h-4 w-4" />
                    {$t('compose.up')}
                  </button>
                  <button
                    class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-purple-600 transition-colors hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-800 dark:text-purple-400 dark:hover:bg-gray-700"
                    onclick={() => upMutation.mutate({ projectName: project.name, forceRecreate: true })}
                  >
                    <Zap class="h-4 w-4" />
                    {$t('compose.forceRecreate')}
                  </button>
                {:else if project.availableActions?.start}
                  <button
                    class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-green-600 transition-colors hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-800 dark:text-green-400 dark:hover:bg-gray-700"
                    onclick={() => restartMutation.mutate(project.name)}
                  >
                    <Play class="h-4 w-4" />
                    {$t('containers.start')}
                  </button>
                {/if}
              {/if}

              {#if project.state === EntityState.Running || project.state === EntityState.Degraded || project.state === EntityState.Restarting}
                <button
                  class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-blue-600 transition-colors hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-800 dark:text-blue-400 dark:hover:bg-gray-700"
                  onclick={() => restartMutation.mutate(project.name)}
                >
                  <RotateCw class="h-4 w-4" />
                  {$t('compose.restart')}
                </button>
                <button
                  class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-yellow-600 transition-colors hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-800 dark:text-yellow-400 dark:hover:bg-gray-700"
                  onclick={() => stopMutation.mutate(project.name)}
                >
                  <Square class="h-4 w-4" />
                  {$t('compose.stop')}
                </button>
              {/if}

              {#if project.state !== EntityState.Down && project.state !== EntityState.NotStarted && project.availableActions?.down}
                <button
                  class="flex min-h-10 items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white px-2 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 dark:border-gray-700 dark:bg-gray-800 dark:text-red-400 dark:hover:bg-red-950/30"
                  onclick={() => handleRemoveProject(project)}
                >
                  <Trash2 class="h-4 w-4" />
                  {$t('common.delete')}
                </button>
              {/if}
            </div>
          {/if}
        </article>
      {/each}
    </div>

    <div class="hidden bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-xl border border-gray-200 dark:border-gray-700 overflow-visible shadow hover:shadow-lg transition-all duration-300 md:block">
      <div class="overflow-x-auto">
        <table class="w-full">
          <DraggableTableHeader
            columns={projectColumns}
            columnOrder={projectColumnPrefs.order}
            sortKey={filters.sortKey}
            sortDir={filters.sortDir}
            onSort={toggleProjectSort}
            onReorder={handleProjectColumnReorder}
          />
          <tbody class="divide-y divide-gray-100 dark:divide-gray-700">
            {#each filteredAndSortedProjects as project (project.name)}
              {@const projectState = getProjectState(project.name)}
              {@const isOpen = projectState.isOpen}
              <!-- Project Row -->
              <tr
                class="hover:bg-white dark:hover:bg-gray-800 transition-all cursor-pointer"
                onclick={(e) => {
                  const target = e.target as HTMLElement;
                  if (!target.closest('button')) {
                    toggleProjectOpen(project.name);
                  }
                }}
              >
                {#each projectColumnPrefs.order as colId (colId)}
                  {#if colId === 'name'}
                    <td class="px-4 py-3">
                      <div class="flex items-center gap-2">
                        <span
                          class="inline-block transition-transform duration-150 ease-in-out text-gray-500 dark:text-gray-400"
                          class:rotate-90={isOpen}
                        >
                          <ChevronRight class="w-4 h-4" />
                        </span>
                        <div class="flex items-center gap-2 min-w-0">
                          <button
                            class="text-sm font-medium text-blue-600 dark:text-blue-400 hover:underline focus:outline-none cursor-pointer shrink-0"
                            onclick={(e) => {
                              e.stopPropagation();
                              navigateToProject(project.name);
                            }}
                            title={$t('compose.projectDetails')}
                          >
                            {project.name}
                          </button>
                          <ActionStatusBadge entityType="project" entityId={project.name} />
                          {#if project.path}
                            <span class="text-xs italic text-gray-500 dark:text-gray-400 truncate" title={project.path}>
                              {project.path}
                            </span>
                          {/if}
                        </div>
                      </div>
                      {#if project.warning}
                        <div class="flex items-center gap-1 text-xs text-amber-600 dark:text-amber-400 mt-0.5 ml-6">
                          <svg class="w-3 h-3 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                          </svg>
                          <span class="truncate">{project.warning}</span>
                        </div>
                      {/if}
                    </td>
                  {:else if colId === 'state'}
                    <td class="px-4 py-3">
                      <div class="flex items-center gap-1.5">
                        <StateBadge status={project.state} size="sm" class="shrink-0 whitespace-nowrap" />
                        <CrashLoopBadge entityType="project" entityId={project.name} />
                        {#if project.autoUpdateEnabled === false}
                          <span
                            class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500 dark:bg-gray-700 dark:text-gray-400"
                            title={$t('compose.autoUpdateDisabledHint')}
                          >
                            <RefreshCwOff class="w-3 h-3" />
                            {$t('compose.autoUpdateDisabled')}
                          </span>
                        {/if}
                      </div>
                    </td>
                  {:else if colId === 'services'}
                    <td class="px-4 py-3">
                      <span class="text-xs text-gray-700 dark:text-gray-300">
                        {project.services?.length ?? 0}
                      </span>
                    </td>
                  {:else if colId === 'actions'}
                    <td class="px-4 py-3">
                      <div class="flex items-center gap-1">
                        <!-- Check Updates Button (admin only, when compose file exists) -->
                        {#if isAdmin.current && project.hasComposeFile}
                          <div class="relative">
                            <ActionButton
                              icon={checkingUpdatesFor === project.name ? Loader2 : Download}
                              variant="update"
                              title={$t('update.checkUpdates')}
                              disabled={checkingUpdatesFor === project.name}
                              class={checkingUpdatesFor === project.name ? 'animate-spin' : ''}
                              onclick={(e) => {
                                e.stopPropagation();
                                handleCheckUpdates(project.name);
                              }}
                            />
                            {#if projectHasUpdates(project.name) || (project.servicesWithUpdates != null && project.servicesWithUpdates > 0)}
                              <span class="absolute -top-0.5 -right-0.5 w-2 h-2 bg-red-500 rounded-full"></span>
                            {/if}
                          </div>
                        {/if}
                        {#if project.state === EntityState.Down || project.state === EntityState.Stopped || project.state === EntityState.Exited || project.state === EntityState.Degraded || project.state === EntityState.Restarting || project.state === EntityState.Created || project.state === EntityState.NotStarted}
                          {#if project.availableActions?.up}
                            <ActionButton
                              icon={Play}
                              variant="play"
                              title={$t('compose.up')}
                              onclick={(e) => { e.stopPropagation(); upMutation.mutate({ projectName: project.name }); }}
                            />
                            <ActionButton
                              icon={Zap}
                              variant="force"
                              title={$t('compose.forceRecreate')}
                              onclick={(e) => { e.stopPropagation(); upMutation.mutate({ projectName: project.name, forceRecreate: true }); }}
                            />
                          {:else if project.availableActions?.start}
                            <ActionButton
                              icon={Play}
                              variant="play"
                              title={$t('containers.start')}
                              onclick={(e) => { e.stopPropagation(); restartMutation.mutate(project.name); }}
                            />
                          {/if}
                        {/if}
                        {#if project.state === EntityState.Running || project.state === EntityState.Degraded || project.state === EntityState.Restarting}
                          <ActionButton
                            icon={RotateCw}
                            variant="restart"
                            title={$t('compose.restart')}
                            onclick={(e) => { e.stopPropagation(); restartMutation.mutate(project.name); }}
                          />
                          <ActionButton
                            icon={Square}
                            variant="stop"
                            title={$t('compose.stop')}
                            onclick={(e) => { e.stopPropagation(); stopMutation.mutate(project.name); }}
                          />
                        {/if}
                        {#if project.state !== EntityState.Down && project.state !== EntityState.NotStarted && project.availableActions?.down}
                          <ActionButton
                            icon={Trash2}
                            variant="remove"
                            title={$t('common.delete')}
                            onclick={(e) => { e.stopPropagation(); handleRemoveProject(project); }}
                          />
                        {/if}
                      </div>
                    </td>
                  {/if}
                {/each}
              </tr>
              <!-- Expanded Services Row -->
              {#if isOpen && project.services && project.services.length > 0}
                <tr>
                  <td colspan="4" class="p-0">
                    <div class="bg-gray-50 dark:bg-gray-900 border-t border-gray-200 dark:border-gray-700">
                      <div class="overflow-x-auto">
                        <table class="w-full table-fixed">
                          <DraggableTableHeader
                            columns={serviceColumns}
                            columnOrder={serviceColumnPrefs.order}
                            sortKey={projectState.serviceSortKey}
                            sortDir={projectState.serviceSortDir}
                            onSort={(key) => toggleServiceSort(project.name, key)}
                            onReorder={handleServiceColumnReorder}
                          />
                          <tbody class="divide-y divide-gray-100 dark:divide-gray-700">
                            {#each getSortedServices(project) as service (service.id)}
                              <tr class="hover:bg-white dark:hover:bg-gray-800 transition-all">
                                {#each serviceColumnPrefs.order as colId (colId)}
                                  {#if colId === 'name'}
                                    <td class="pl-10 pr-4 py-2">
                                      <button
                                        class="text-xs font-medium text-blue-600 dark:text-blue-400 hover:underline focus:outline-none cursor-pointer truncate block"
                                        onclick={() => goto(`/containers/${service.id}`)}
                                        title={$t('containers.viewDetails')}
                                      >
                                        {service.name}
                                      </button>
                                      <div
                                        class="text-[10px] text-gray-500 dark:text-gray-400 font-mono truncate"
                                        title={service.id}
                                      >
                                        {service.id}
                                      </div>
                                    </td>
                                  {:else if colId === 'image'}
                                    <td class="px-4 py-2">
                                      <div
                                        class="text-xs text-gray-900 dark:text-gray-300 truncate"
                                        title={service.image || '-'}
                                      >
                                        {service.image || '-'}
                                      </div>
                                    </td>
                                  {:else if colId === 'ipAddress'}
                                    <td class="px-4 py-2">
                                      <div class="text-xs text-gray-500 dark:text-gray-400 font-mono truncate" title={service.ipAddress || '-'}>
                                        {service.ipAddress || '-'}
                                      </div>
                                    </td>
                                  {:else if colId === 'ports'}
                                    <td class="px-4 py-2">
                                      <div class="text-xs text-gray-500 dark:text-gray-400 font-mono">
                                        {#if service.ports && service.ports.length > 0}
                                          {#each service.ports as port}
                                            <div>{port}</div>
                                          {/each}
                                        {:else}
                                          -
                                        {/if}
                                      </div>
                                    </td>
                                  {:else if colId === 'state'}
                                    <td class="px-4 py-2">
                                      <div class="flex items-center gap-1.5">
                                        <StateBadge status={service.state} size="sm" />
                                        <CrashLoopBadge entityType="container" entityId={service.id} />
                                      </div>
                                    </td>
                                  {:else if colId === 'status'}
                                    <td class="px-4 py-2">
                                      <div
                                        class="text-xs text-gray-500 dark:text-gray-400 truncate"
                                        title={service.status || '-'}
                                      >
                                        {service.status || '-'}
                                      </div>
                                    </td>
                                  {:else if colId === 'actions'}
                                    <td class="px-4 py-2 text-xs">
                                      <div class="flex items-center gap-1">
                                        {#if service.state === EntityState.Unknown || service.state === EntityState.NotStarted}
                                          <span class="text-gray-400 text-xs italic">{$t('containers.noContainer')}</span>
                                        {:else if service.state === EntityState.Running}
                                          <ActionButton
                                            icon={RotateCw}
                                            variant="restart"
                                            title={$t('containers.restart')}
                                            onclick={() => restartContainerMutation.mutate(service.id)}
                                          />
                                          <ActionButton
                                            icon={Square}
                                            variant="stop"
                                            title={$t('containers.stop')}
                                            onclick={() => stopContainerMutation.mutate(service.id)}
                                          />
                                          <ActionButton
                                            icon={Trash2}
                                            variant="remove"
                                            title={$t('containers.remove')}
                                            onclick={() => handleRemoveService(service)}
                                          />
                                        {:else}
                                          <ActionButton
                                            icon={Play}
                                            variant="play"
                                            title={$t('containers.start')}
                                            onclick={() => startContainerMutation.mutate(service.id)}
                                          />
                                          <ActionButton
                                            icon={Trash2}
                                            variant="remove"
                                            title={$t('containers.remove')}
                                            onclick={() => handleRemoveService(service)}
                                          />
                                        {/if}
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
                  </td>
                </tr>
              {/if}
            {/each}
          </tbody>
        </table>
      </div>
    </div>
  {/if}
</div>

<!-- Service Update Dialog -->
<ServiceUpdateDialog
  open={updateDialogOpen}
  projectName={selectedProjectForUpdate ?? ''}
  updateCheck={projectUpdateCheck}
  onClose={closeUpdateDialog}
/>

<ConfirmDialog
  open={confirmDialog.open}
  title={confirmDialog.title}
  description={confirmDialog.description}
  onconfirm={confirmDialog.onConfirm}
  oncancel={() => confirmDialog.open = false}
/>

<!-- Bulk Update Dialog -->
<BulkUpdateDialog
  open={bulkUpdateDialogOpen}
  onClose={() => bulkUpdateDialogOpen = false}
/>
