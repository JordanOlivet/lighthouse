<script lang="ts">
	import { page } from '$app/stores';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		ArrowLeft,
		Play,
		Square,
		RotateCw,
		Trash2,
		Zap,
		RefreshCw,
		Download,
		Loader2
	} from 'lucide-svelte';
	import { composeApi, containersApi } from '$lib/api';
	import { updateApi } from '$lib/api/update';
	import { EntityState, type ComposeService } from '$lib/types';
	import type { ProjectUpdateCheckResponse } from '$lib/types/update';
	import StateBadge from '$lib/components/common/StateBadge.svelte';
	import LoadingState from '$lib/components/common/LoadingState.svelte';
	import ProjectInfoSection from '$lib/components/compose/ProjectInfoSection.svelte';
	import StatsCard from '$lib/components/stats/StatsCard.svelte';
	import LogViewer from '$lib/components/log-viewer/LogViewer.svelte';
	import ServiceUpdateDialog from '$lib/components/update/ServiceUpdateDialog.svelte';
	import ActionButton from '$lib/components/common/ActionButton.svelte';
	import { t } from '$lib/i18n';
	import { toast } from 'svelte-sonner';
	import { goto } from '$app/navigation';
	import { isAdmin } from '$lib/stores/auth.svelte';
	import { projectHasUpdates } from '$lib/stores/projectUpdate.svelte';

	const projectName = $derived(
		$page.params.projectName ? decodeURIComponent($page.params.projectName) : ''
	);

	const queryClient = useQueryClient();

	// SSE is now handled globally in the protected layout
	// The SSE-Query bridge automatically invalidates queries on events
	const projectQuery = createQuery(() => ({
		queryKey: ['compose', 'project', projectName],
		queryFn: () => composeApi.getProjectDetails(projectName),
		enabled: !!projectName,
		refetchInterval: false,
		refetchOnWindowFocus: false,
		refetchOnReconnect: false,
		staleTime: 0
	}));

	// Project mutations
	// Note: The SSE-Query bridge handles cache invalidation automatically
	const upMutation = createMutation(() => ({
		mutationFn: ({ detach, forceRecreate }: { detach?: boolean; forceRecreate?: boolean }) =>
			composeApi.upProject(projectName, { detach, forceRecreate }),
		onSuccess: () => toast.success($t('compose.upSuccess')),
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	const downMutation = createMutation(() => ({
		mutationFn: () => composeApi.downProject(projectName),
		onSuccess: () => toast.success($t('compose.downSuccess')),
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	const restartMutation = createMutation(() => ({
		mutationFn: () => composeApi.restartProject(projectName),
		onSuccess: () => toast.success($t('compose.restartSuccess')),
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	const stopMutation = createMutation(() => ({
		mutationFn: () => composeApi.stopProject(projectName),
		onSuccess: () => toast.success($t('compose.stopSuccess')),
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	const startMutation = createMutation(() => ({
		mutationFn: () => composeApi.startProject(projectName),
		onSuccess: () => toast.success($t('compose.startSuccess')),
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	// Container mutations
	const startContainerMutation = createMutation(() => ({
		mutationFn: (containerId: string) => containersApi.start(containerId),
		onSuccess: () => toast.success($t('containers.startSuccess')),
		onError: () => toast.error($t('containers.failedToStart'))
	}));

	const stopContainerMutation = createMutation(() => ({
		mutationFn: (containerId: string) => containersApi.stop(containerId),
		onSuccess: () => toast.success($t('containers.stopSuccess')),
		onError: () => toast.error($t('containers.failedToStop'))
	}));

	const restartContainerMutation = createMutation(() => ({
		mutationFn: (containerId: string) => containersApi.restart(containerId),
		onSuccess: () => toast.success($t('containers.restartSuccess')),
		onError: () => toast.error($t('containers.failedToRestart'))
	}));

	const removeContainerMutation = createMutation(() => ({
		mutationFn: ({ containerId, force }: { containerId: string; force: boolean }) =>
			containersApi.remove(containerId, force),
		onSuccess: () => toast.success($t('containers.removeSuccess')),
		onError: () => toast.error($t('containers.failedToRemove'))
	}));

	// Update dialog state
	let updateDialogOpen = $state(false);
	let projectUpdateCheck = $state<ProjectUpdateCheckResponse | null>(null);
	let checkingUpdates = $state(false);

	// Check updates mutation
	const checkUpdatesMutation = createMutation(() => ({
		mutationFn: () => updateApi.checkProjectUpdates(projectName, true),
		onSuccess: (data: ProjectUpdateCheckResponse) => {
			projectUpdateCheck = data;
			updateDialogOpen = true;
			checkingUpdates = false;
		},
		onError: (error: Error) => {
			toast.error($t('update.checkFailed') + ': ' + error.message);
			checkingUpdates = false;
		}
	}));

	function handleCheckUpdates() {
		checkingUpdates = true;
		checkUpdatesMutation.mutate();
	}

	function closeUpdateDialog() {
		updateDialogOpen = false;
		projectUpdateCheck = null;
	}

	function handleRemoveComposeProject(projectState: string) {
		const isRunning = projectState === 'Running';
		const message = isRunning
			? $t('compose.confirmRemoveRunningWithName').replace('{name}', projectName)
			: $t('compose.confirmRemoveWithName').replace('{name}', projectName);

		if (confirm(message)) {
			downMutation.mutate();
		}
	}

	function handleRemoveContainer(service: ComposeService) {
		const isRunning = service.state === 'Running';
		const message = isRunning
			? $t('containers.confirmRemoveRunningWithName').replace('{name}', service.name)
			: $t('containers.confirmRemoveWithName').replace('{name}', service.name);

		if (confirm(message)) {
			removeContainerMutation.mutate({ containerId: service.id, force: isRunning });
		}
	}
</script>

<div class="space-y-8">
	<!-- Page Header -->
	<div class="mb-8">
		<div class="flex items-center justify-between">
			<div class="flex items-center gap-4">
				<a
					href="/compose/projects"
					class="p-2 text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white hover:bg-gray-100 dark:hover:bg-gray-700 rounded-lg transition-colors"
					title="Back to projects"
				>
					<ArrowLeft class="w-5 h-5" />
				</a>
				<div>
					<h1 class="text-4xl font-bold text-gray-900 dark:text-white mb-3">
						{projectName}
					</h1>
					<p class="text-lg text-gray-600 dark:text-gray-400">{$t('compose.projectDetails')}</p>
				</div>
			</div>
			<button
				onclick={() => projectQuery.refetch()}
				class="flex items-center gap-2 px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-800 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
				title={$t('common.refresh')}
			>
				<RefreshCw class="w-4 h-4" />
				{$t('common.refresh')}
			</button>
		</div>
	</div>

	{#if projectQuery.isLoading}
		<LoadingState message={$t('compose.loadingDetails')} />
	{:else if projectQuery.error}
		<div class="text-center py-8">
			<p class="text-red-500">{$t('compose.failedToLoadProject')}</p>
			<button
				onclick={() => history.back()}
				class="mt-4 px-4 py-2 bg-gray-200 dark:bg-gray-700 rounded-lg hover:bg-gray-300 dark:hover:bg-gray-600 transition-colors"
			>
				{$t('compose.backToProjects')}
			</button>
		</div>
	{:else if projectQuery.data}
		{@const project = projectQuery.data}

		<!-- Row 1: Services Table -->
		<div
			class="bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-2xl border border-gray-200 dark:border-gray-700 overflow-visible shadow-lg hover:shadow-2xl transition-all duration-300"
		>
			<!-- Project Header -->
			<div
				class="bg-white dark:bg-gray-800 px-6 py-4 rounded-t-2xl border-b border-gray-200 dark:border-gray-700"
			>
					<div class="flex items-center justify-between">
					<div class="flex items-center gap-4 min-w-0 flex-1">
						<h3 class="text-lg font-semibold text-gray-900 dark:text-white flex-shrink-0">
							{project.name}
						</h3>
						<StateBadge status={project.state} />
						<!-- Compose file path and warning inline -->
						{#if project.composeFilePath}
							<span class="text-sm text-gray-500 dark:text-gray-400 truncate hidden sm:inline" title={project.composeFilePath}>
								{project.composeFilePath}
							</span>
						{/if}
						{#if project.warning}
							<span class="flex items-center gap-1 text-sm text-amber-600 dark:text-amber-400 flex-shrink-0" title={project.warning}>
								<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
									<path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
								</svg>
								<span class="hidden md:inline">{project.warning}</span>
							</span>
						{/if}
					</div>
					<div class="flex gap-2 flex-shrink-0">
						<!-- Check Updates Button (admin only, when compose file exists) -->
						{#if isAdmin.current && project.hasComposeFile}
							<div class="relative">
								<ActionButton
									icon={checkingUpdates ? Loader2 : Download}
									variant="update"
									title={$t('update.checkUpdates')}
									disabled={checkingUpdates}
									class={checkingUpdates ? 'animate-spin' : ''}
									onclick={handleCheckUpdates}
								/>
								{#if projectHasUpdates(projectName)}
									<span class="absolute -top-0.5 -right-0.5 w-2 h-2 bg-red-500 rounded-full"></span>
								{/if}
							</div>
						{/if}

						<!-- UP: For "Not Started" or "Restarting" (crash loop) projects with compose file -->
						{#if (project.state === EntityState.NotStarted || project.state === EntityState.Restarting) && project.availableActions?.up}
							<ActionButton
								icon={Play}
								variant="play"
								title={$t('compose.up')}
								onclick={() => upMutation.mutate({ detach: true })}
							/>
							<ActionButton
								icon={Zap}
								variant="force"
								title={$t('compose.forceRecreate')}
								onclick={() => upMutation.mutate({ detach: true, forceRecreate: true })}
							/>

						<!-- START: For stopped containers (Stopped, Exited, Down, Created) -->
						{:else if (project.state === EntityState.Stopped ||
						           project.state === EntityState.Exited ||
						           project.state === EntityState.Down ||
						           project.state === EntityState.Created) &&
						          project.availableActions?.start}
							<ActionButton
								icon={Play}
								variant="play"
								title={$t('containers.start')}
								onclick={() => startMutation.mutate()}
							/>
						{/if}

						<!-- RESTART & STOP: For running projects -->
						{#if project.state === EntityState.Running || project.state === EntityState.Degraded || project.state === EntityState.Restarting}
							<ActionButton
								icon={RotateCw}
								variant="restart"
								title={$t('compose.restart')}
								onclick={() => restartMutation.mutate()}
							/>
							<ActionButton
								icon={Square}
								variant="stop"
								title={$t('compose.stop')}
								onclick={() => stopMutation.mutate()}
							/>
						{/if}

						<!-- DOWN: To remove (if containers exist) -->
						{#if project.state !== EntityState.NotStarted && project.availableActions?.down}
							<ActionButton
								icon={Trash2}
								variant="remove"
								title={$t('common.delete')}
								onclick={() => handleRemoveComposeProject(project.state)}
							/>
						{/if}
					</div>
				</div>
			</div>

			<!-- Services Table -->
			{#if !project.services || project.services.length === 0}
				<div class="p-6 text-center text-gray-500 dark:text-gray-400">
					{$t('compose.noServices')}
				</div>
			{:else}
				<div class="overflow-x-auto">
					<table class="w-full">
						<thead
							class="bg-white/50 dark:bg-gray-800/50 border-b border-gray-200 dark:border-gray-700"
						>
							<tr>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.name')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.image')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.ipAddress')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.ports')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.state')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('containers.status')}
								</th>
								<th
									class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider"
								>
									{$t('users.actions')}
								</th>
							</tr>
						</thead>

						<tbody class="divide-y divide-gray-100 dark:divide-gray-700">
							{#each project.services as service (service.id)}
								<tr class="hover:bg-white dark:hover:bg-gray-800 transition-all">
									<td class="px-8 py-5 whitespace-nowrap">
										<button
											class="text-sm font-medium text-blue-600 dark:text-blue-400 hover:underline focus:outline-none cursor-pointer"
											onclick={() => goto(`/containers/${service.id}`)}
											title={$t('containers.viewDetails')}
										>
											{service.name}
										</button>
										<div class="text-xs text-gray-500 dark:text-gray-400 font-mono">
											{service.id}
										</div>
									</td>
									<td class="px-8 py-5">
										<div class="text-sm text-gray-900 dark:text-gray-300">
											{service.image}
										</div>
									</td>
									<td class="px-8 py-5">
										<div class="text-sm text-gray-500 dark:text-gray-400 font-mono">
											{service.ipAddress || '-'}
										</div>
									</td>
									<td class="px-8 py-5">
										<div class="text-sm text-gray-500 dark:text-gray-400 font-mono">
											{#if service.ports && service.ports.length > 0}
												{#each service.ports as port}
													<div>{port}</div>
												{/each}
											{:else}
												-
											{/if}
										</div>
									</td>
									<td class="px-8 py-5 whitespace-nowrap">
										<StateBadge status={service.state} size="sm" />
									</td>
									<td class="px-8 py-5">
										<div class="text-sm text-gray-500 dark:text-gray-400">
											{service.status}
										</div>
									</td>
									<td class="px-8 py-5 whitespace-nowrap text-sm">
										<div class="flex items-center gap-3">
											{#if service.state === 'Running'}
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
											{:else}
												<ActionButton
													icon={Play}
													variant="play"
													title={$t('containers.start')}
													onclick={() => startContainerMutation.mutate(service.id)}
												/>
											{/if}
											<ActionButton
												icon={Trash2}
												variant="remove"
												title={$t('containers.remove')}
												onclick={() => handleRemoveContainer(service)}
											/>
										</div>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</div>

		<!-- Row 2: Compose File Details (left) and Stats (right) -->
		<div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
			<!-- Left: Compose File Details -->
			<div>
				<ProjectInfoSection
					{projectName}
					projectPath={project.composeFilePath ?? undefined}
					hasComposeFile={project.hasComposeFile ?? false}
					canEdit={project.canEdit ?? false}
				/>
			</div>

			<!-- Right: Stats Charts -->
			<div>
				<StatsCard services={project.services} />
			</div>
		</div>

		<!-- Row 3: Logs (full width, resizable) -->
		<div class="w-full">
			<div class="h-[500px] resize-y overflow-auto min-h-[500px] max-h-[1000px]">
				<LogViewer mode="project" {projectName} />
			</div>
		</div>
	{/if}
</div>

<!-- Service Update Dialog -->
<ServiceUpdateDialog
	open={updateDialogOpen}
	{projectName}
	updateCheck={projectUpdateCheck}
	onClose={closeUpdateDialog}
/>
