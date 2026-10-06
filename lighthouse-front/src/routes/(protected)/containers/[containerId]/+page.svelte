<script lang="ts">
	import { page } from '$app/stores';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		ArrowLeft,
		Play,
		Square,
		RotateCw,
		Trash2,
		RefreshCw
	} from 'lucide-svelte';
	import { containersApi } from '$lib/api';
	import StateBadge from '$lib/components/common/StateBadge.svelte';
	import LoadingState from '$lib/components/common/LoadingState.svelte';
	import StatsCard from '$lib/components/stats/StatsCard.svelte';
	import ContainerInfoSection from '$lib/components/compose/ContainerInfoSection.svelte';
	import LogViewer from '$lib/components/log-viewer/LogViewer.svelte';
	import ActionButton from '$lib/components/common/ActionButton.svelte';
	import { t } from '$lib/i18n';
	import { toast } from 'svelte-sonner';
	import { goto } from '$app/navigation';

	const containerId = $derived($page.params.containerId ?? '');

	const queryClient = useQueryClient();

	const containerQuery = createQuery(() => ({
		queryKey: ['container', containerId],
		queryFn: () => containersApi.get(containerId),
		enabled: !!containerId
	}));

	const startMutation = createMutation(() => ({
		mutationFn: () => containersApi.start(containerId),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['container', containerId] });
			toast.success($t('containers.startSuccess'));
		},
		onError: () => toast.error($t('containers.startFailed'))
	}));

	const stopMutation = createMutation(() => ({
		mutationFn: () => containersApi.stop(containerId),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['container', containerId] });
			toast.success($t('containers.stopSuccess'));
		},
		onError: () => toast.error($t('containers.stopFailed'))
	}));

	const restartMutation = createMutation(() => ({
		mutationFn: () => containersApi.restart(containerId),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['container', containerId] });
			toast.success($t('containers.restartSuccess'));
		},
		onError: () => toast.error($t('containers.restartFailed'))
	}));

	const removeMutation = createMutation(() => ({
		mutationFn: ({ force }: { force: boolean }) => containersApi.remove(containerId, force),
		onSuccess: () => {
			toast.success($t('containers.removeSuccess'));
			goto('/containers');
		},
		onError: () => toast.error($t('containers.removeFailed'))
	}));

	function handleRemove() {
		const container = containerQuery.data;
		if (!container) return;

		const isRunning = container.state.toLowerCase() === 'running';
		const message = isRunning
			? $t('containers.confirmRemoveRunningWithName').replace('{name}', container.name)
			: $t('containers.confirmRemoveWithName').replace('{name}', container.name);

		if (confirm(message)) {
			removeMutation.mutate({ force: isRunning });
		}
	}
</script>

<div class="space-y-6 sm:space-y-8">
	<!-- Header -->
	<div class="sm:mb-8">
		<div class="flex items-start justify-between gap-2 sm:items-center">
			<div class="flex min-w-0 items-center gap-2 sm:gap-4">
				<a
					href="/containers"
					class="flex h-10 w-10 shrink-0 items-center justify-center text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white hover:bg-gray-100 dark:hover:bg-gray-700 rounded-lg transition-colors cursor-pointer"
					title={$t('containers.backToContainers')}
				>
					<ArrowLeft class="w-5 h-5" />
				</a>
				<div class="min-w-0">
					<h1 class="break-all text-2xl font-bold text-gray-900 dark:text-white mb-1 sm:mb-3 sm:text-4xl">
						{containerQuery.data?.name || $t('containers.details')}
					</h1>
					<p class="text-sm text-gray-600 dark:text-gray-400 sm:text-lg">{$t('containers.detailsSubtitle')}</p>
				</div>
			</div>
			<button
				onclick={() => containerQuery.refetch()}
				class="flex h-10 w-10 shrink-0 items-center justify-center gap-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-800 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors sm:h-auto sm:w-auto sm:px-4 sm:py-2"
				title={$t('common.refresh')}
				aria-label={$t('common.refresh')}
			>
				<RefreshCw class="w-4 h-4" />
				<span class="hidden sm:inline">{$t('common.refresh')}</span>
			</button>
		</div>
	</div>

	{#if containerQuery.isLoading}
		<LoadingState message={$t('containers.loadingDetails')} />
	{:else if containerQuery.error}
		<div class="text-center py-8">
			<p class="text-red-500">{$t('containers.failedToLoad')}</p>
			<button
				onclick={() => goto('/containers')}
				class="mt-4 px-4 py-2 bg-gray-200 dark:bg-gray-700 rounded-lg hover:bg-gray-300 dark:hover:bg-gray-600 transition-colors"
			>
				{$t('containers.backToContainers')}
			</button>
		</div>
	{:else if containerQuery.data}
		{@const container = containerQuery.data}
		{@const isRunning = container.state.toLowerCase() === 'running'}

		<!-- Container Info Card -->
		<div
			class="bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-2xl border border-gray-200 dark:border-gray-700 overflow-visible shadow-lg hover:shadow-2xl transition-all duration-300"
		>
			<!-- Container Header -->
			<div
				class="bg-white dark:bg-gray-800 px-4 py-3 rounded-t-2xl border-b border-gray-200 dark:border-gray-700 sm:px-6 sm:py-4"
			>
				<div class="flex flex-wrap items-center justify-between gap-3">
					<div class="flex min-w-0 flex-1 flex-wrap items-center gap-x-4 gap-y-1">
						<h3 class="min-w-0 truncate text-base font-semibold sm:text-lg text-gray-900 dark:text-white" title={container.name}>
							{container.name}
						</h3>
						<StateBadge status={container.state} />
						<span class="text-sm text-gray-500 dark:text-gray-400 font-mono">
							{container.id.substring(0, 12)}
						</span>
					</div>
					<div class="flex gap-2 flex-shrink-0">
						{#if isRunning}
							<ActionButton
								icon={RotateCw}
								variant="restart"
								title={$t('containers.restart')}
								disabled={restartMutation.isPending}
								onclick={() => restartMutation.mutate()}
							/>
							<ActionButton
								icon={Square}
								variant="stop"
								title={$t('containers.stop')}
								disabled={stopMutation.isPending}
								onclick={() => stopMutation.mutate()}
							/>
						{:else}
							<ActionButton
								icon={Play}
								variant="play"
								title={$t('containers.start')}
								disabled={startMutation.isPending}
								onclick={() => startMutation.mutate()}
							/>
						{/if}
						<ActionButton
							icon={Trash2}
							variant="remove"
							title={$t('containers.remove')}
							disabled={removeMutation.isPending}
							onclick={handleRemove}
						/>
					</div>
				</div>
			</div>

			<!-- Container Details: stacked list on mobile (state is already in the header) -->
			<dl class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 px-4 py-3 text-sm md:hidden">
				<dt class="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">{$t('containers.image')}</dt>
				<dd class="break-all text-gray-900 dark:text-gray-300">{container.image}</dd>
				<dt class="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">{$t('containers.ipAddress')}</dt>
				<dd class="break-all font-mono text-gray-500 dark:text-gray-400">{container.ipAddress || '-'}</dd>
				<dt class="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">{$t('containers.ports')}</dt>
				<dd class="font-mono text-gray-500 dark:text-gray-400">
					{#if container.ports && container.ports.length > 0}
						{#each container.ports as port}
							<div class="break-all">{port}</div>
						{/each}
					{:else}
						-
					{/if}
				</dd>
				<dt class="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">{$t('containers.status')}</dt>
				<dd class="text-gray-500 dark:text-gray-400">{container.status}</dd>
			</dl>

			<!-- Container Details Table -->
			<div class="hidden overflow-x-auto md:block">
				<table class="w-full">
					<thead class="bg-white/50 dark:bg-gray-800/50 border-b border-gray-200 dark:border-gray-700">
						<tr>
							<th class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider">
								{$t('containers.image')}
							</th>
							<th class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider">
								{$t('containers.ipAddress')}
							</th>
							<th class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider">
								{$t('containers.ports')}
							</th>
							<th class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider">
								{$t('containers.state')}
							</th>
							<th class="px-8 py-5 text-left text-xs font-bold text-gray-700 dark:text-gray-300 uppercase tracking-wider">
								{$t('containers.status')}
							</th>
						</tr>
					</thead>
					<tbody>
						<tr>
							<td class="px-8 py-5">
								<div class="text-sm text-gray-900 dark:text-gray-300">
									{container.image}
								</div>
							</td>
							<td class="px-8 py-5">
								<div class="text-sm text-gray-500 dark:text-gray-400 font-mono">
									{container.ipAddress || '-'}
								</div>
							</td>
							<td class="px-8 py-5">
								<div class="text-sm text-gray-500 dark:text-gray-400 font-mono">
									{#if container.ports && container.ports.length > 0}
										{#each container.ports as port}
											<div>{port}</div>
										{/each}
									{:else}
										-
									{/if}
								</div>
							</td>
							<td class="px-8 py-5 whitespace-nowrap">
								<StateBadge status={container.state} size="sm" />
							</td>
							<td class="px-8 py-5">
								<div class="text-sm text-gray-500 dark:text-gray-400">
									{container.status}
								</div>
							</td>
						</tr>
					</tbody>
				</table>
			</div>
		</div>

		<!-- Details Section: Two Columns -->
		<div class="grid grid-cols-1 md:grid-cols-2 gap-4 sm:gap-6">
			<!-- Left: Technical Details -->
			<div>
				<ContainerInfoSection {container} />
			</div>

			<!-- Right: Live Resource Stats -->
			<div>
				<StatsCard {containerId} isActive={isRunning} />
			</div>
		</div>

		<!-- Logs Section -->
		<div class="w-full h-[75dvh] min-h-[300px] md:h-[400px] md:max-h-[800px] md:resize-y md:overflow-auto">
			<LogViewer mode="container" containerId={container.id} containerName={container.name} />
		</div>
	{/if}
</div>
