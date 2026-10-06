<script lang="ts">
	import { untrack } from 'svelte';
	import { createQuery } from '@tanstack/svelte-query';
	import { containersApi } from '$lib/api';
	import { Activity, Cpu, HardDrive, MemoryStick, Network } from 'lucide-svelte';
	import { t } from '$lib/i18n';
	import { logger } from '$lib/utils/logger';
	import StreamingLineChart from '$lib/components/charts/StreamingLineChart.svelte';
	import type { ComposeService, ContainerStats } from '$lib/types';
	import {
		formatBytes,
		getBestMemoryUnit,
		getBestNetworkRateUnit,
		getBestDiskRateUnit
	} from '$lib/utils/units';

	interface Props {
		// Mode 1: Container unique
		containerId?: string;
		isActive?: boolean; // Requis si containerId est fourni

		// Mode 2: Services multiples (projet compose)
		services?: ComposeService[];

		// Titre personnalisé (optionnel)
		title?: string;
	}

	let { containerId, isActive: isActiveProp, services, title }: Props = $props();

	interface AggregatedStats {
		cpuPercentage: number;
		memoryUsage: number;
		memoryLimit: number;
		memoryPercentage: number;
		networkRx: number;
		networkTx: number;
		diskRead: number;
		diskWrite: number;
		timestamp: Date;
	}

	interface RateStats {
		networkRxRate: number;
		networkTxRate: number;
		diskReadRate: number;
		diskWriteRate: number;
		timestamp: Date;
	}

	let statsHistory = $state<AggregatedStats[]>([]);
	let rateHistory = $state<RateStats[]>([]);
	let currentStats = $state<AggregatedStats | null>(null);
	let currentRates = $state<RateStats | null>(null);
	// Use plain variables (not reactive) to avoid circular dependency in effects
	let previousStatsRef: AggregatedStats | null = null;

	// Détection automatique du mode et de l'état actif
	const mode = $derived(containerId ? 'container' : 'project');
	const isActive = $derived(
		mode === 'container'
			? (isActiveProp ?? false)
			: (services?.some((s) => s.state === 'Running') ?? false)
	);

	// IDs des containers à interroger
	const containerIds = $derived(
		mode === 'container'
			? [containerId!]
			: (services?.filter((s) => s.state === 'Running').map((s) => s.id) ?? [])
	);

	// Titre dynamique
	const displayTitle = $derived(
		title ?? (mode === 'container' ? $t('containers.liveResourceStats') : $t('common.projectStatistics'))
	);

	// Message quand inactif
	const inactiveMessage = $derived(
		mode === 'container' ? $t('containers.containerNotRunning') : $t('common.noRunningServices')
	);

	// Visible time window selector. The in-memory buffer always keeps ~5 min,
	// so all options render instantly without any extra fetching.
	// (Windows of 1h+ would require a backend time-series feature.)
	const WINDOWS = [
		{ label: '1m', ms: 60_000 },
		{ label: '2m', ms: 120_000 },
		{ label: '5m', ms: 300_000 }
	] as const;
	let windowMs = $state<number>(60_000);

	// Fetch stats for all containers every 1 second
	const statsQuery = createQuery(() => ({
		queryKey: ['stats', ...containerIds.sort()],
		queryFn: async () => {
			const statsPromises = containerIds.map(async (id) => {
				try {
					return await containersApi.getStats(id);
				} catch (error: any) {
					// Don't log 404 errors - container was probably stopped/removed
					if (error?.response?.status !== 404) {
						logger.error(`Failed to fetch stats for container ${id}:`, error);
					}
					return null;
				}
			});

			const stats = await Promise.all(statsPromises);
			return stats.filter((s): s is ContainerStats => s !== null);
		},
		refetchInterval: 1000,
		enabled: isActive && containerIds.length > 0,
		retry: false
	}));

	// Update current stats and history when new data arrives.
	// Depend on `dataUpdatedAt` (bumped on every fetch) rather than `data`:
	// TanStack Query structural-sharing keeps the same `data` reference when the
	// values are unchanged (idle container), which would otherwise freeze sampling.
	$effect(() => {
		const updatedAt = statsQuery.dataUpdatedAt;
		const allStats = statsQuery.data;
		if (!updatedAt || !allStats || allStats.length === 0) return;

		const aggregated: AggregatedStats = {
			cpuPercentage: 0,
			memoryUsage: 0,
			memoryLimit: 0,
			memoryPercentage: 0,
			networkRx: 0,
			networkTx: 0,
			diskRead: 0,
			diskWrite: 0,
			timestamp: new Date()
		};

		allStats.forEach((stats: ContainerStats) => {
			aggregated.cpuPercentage += stats.cpuPercentage;
			aggregated.memoryUsage += stats.memoryUsage;
			aggregated.memoryLimit += stats.memoryLimit;
			aggregated.networkRx += stats.networkRx;
			aggregated.networkTx += stats.networkTx;
			aggregated.diskRead += stats.diskRead;
			aggregated.diskWrite += stats.diskWrite;
		});

		// Calculate average memory percentage
		if (aggregated.memoryLimit > 0) {
			aggregated.memoryPercentage = (aggregated.memoryUsage / aggregated.memoryLimit) * 100;
		}

		// Calculate rates (bytes per second) based on difference from previous stats
		let newRates: RateStats | null = null;
		if (previousStatsRef) {
			const timeDiff = (aggregated.timestamp.getTime() - previousStatsRef.timestamp.getTime()) / 1000;
			if (timeDiff > 0) {
				newRates = {
					networkRxRate: Math.max(0, (aggregated.networkRx - previousStatsRef.networkRx) / timeDiff),
					networkTxRate: Math.max(0, (aggregated.networkTx - previousStatsRef.networkTx) / timeDiff),
					diskReadRate: Math.max(0, (aggregated.diskRead - previousStatsRef.diskRead) / timeDiff),
					diskWriteRate: Math.max(0, (aggregated.diskWrite - previousStatsRef.diskWrite) / timeDiff),
					timestamp: aggregated.timestamp
				};
			}
		}

		previousStatsRef = aggregated;

		// Update history - use untrack to avoid creating dependencies on history arrays
		const fiveMinutesAgo = new Date(Date.now() - 5 * 60 * 1000);

		untrack(() => {
			statsHistory = [...statsHistory, aggregated].filter((stat) => stat.timestamp >= fiveMinutesAgo);

			if (newRates) {
				rateHistory = [...rateHistory, newRates].filter((stat) => stat.timestamp >= fiveMinutesAgo);
			}
		});

		currentRates = newRates;
		currentStats = aggregated;
	});

	// Reset history when services stop
	$effect(() => {
		if (!isActive) {
			untrack(() => {
				statsHistory = [];
				rateHistory = [];
				previousStatsRef = null;
				currentStats = null;
				currentRates = null;
			});
		}
	});

	// Use utility functions to get best units based on data
	const memoryUnit = $derived(getBestMemoryUnit(statsHistory, (s) => s.memoryUsage));
	const networkRateUnit = $derived(
		getBestNetworkRateUnit(rateHistory, (s) => Math.max(s.networkRxRate, s.networkTxRate))
	);
	const diskRateUnit = $derived(
		getBestDiskRateUnit(rateHistory, (s) => Math.max(s.diskReadRate, s.diskWriteRate))
	);

	// Prepare chart data
	const cpuChartData = $derived(
		statsHistory.map((stat) => ({
			timestamp: stat.timestamp,
			cpu: stat.cpuPercentage
		}))
	);

	const memoryChartData = $derived(
		statsHistory.map((stat) => ({
			timestamp: stat.timestamp,
			memory: stat.memoryUsage / memoryUnit.divisor
		}))
	);

	const networkChartData = $derived(
		rateHistory.map((stat) => ({
			timestamp: stat.timestamp,
			rx: stat.networkRxRate / networkRateUnit.divisor,
			tx: stat.networkTxRate / networkRateUnit.divisor
		}))
	);

	const diskChartData = $derived(
		rateHistory.map((stat) => ({
			timestamp: stat.timestamp,
			read: stat.diskReadRate / diskRateUnit.divisor,
			write: stat.diskWriteRate / diskRateUnit.divisor
		}))
	);
</script>

{#if !isActive}
	<div
		class="bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg p-4 sm:p-6"
	>
		<div class="flex items-center gap-2 mb-4">
			<Activity class="h-4 w-4 text-gray-600 dark:text-gray-400 sm:h-5 sm:w-5" />
			<h3 class="text-sm font-semibold text-gray-900 dark:text-white sm:text-base">
				{displayTitle}
			</h3>
		</div>
		<p class="text-sm text-gray-600 dark:text-gray-400">{inactiveMessage}</p>
	</div>
{:else}
	<div
		class="bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg hover:shadow-2xl transition-all duration-300 overflow-hidden"
	>
		<!-- Header -->
		<div
			class="flex items-center justify-between gap-3 px-4 py-2 sm:px-5 sm:py-3 border-b border-gray-200 dark:border-gray-700"
		>
			<div class="flex items-center gap-2 min-w-0">
				<Activity class="h-4 w-4 text-gray-600 dark:text-gray-400 flex-shrink-0 sm:h-5 sm:w-5" />
				<h3 class="text-sm font-semibold text-gray-900 dark:text-white truncate sm:text-base">{displayTitle}</h3>
			</div>
			<!-- Time window selector -->
			<div
				class="flex items-center gap-0.5 rounded-lg bg-gray-100 dark:bg-gray-900/60 p-0.5 flex-shrink-0"
				role="group"
				aria-label={$t('containers.timeWindow')}
			>
				{#each WINDOWS as w (w.ms)}
					<button
						type="button"
						onclick={() => (windowMs = w.ms)}
						aria-pressed={windowMs === w.ms}
						class="rounded-md px-2.5 py-1 text-xs font-semibold transition-colors cursor-pointer {windowMs ===
						w.ms
							? 'bg-white dark:bg-gray-700 text-gray-900 dark:text-white shadow-sm'
							: 'text-gray-500 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200'}"
					>
						{w.label}
					</button>
				{/each}
			</div>
		</div>

		<!-- Stats Content -->
		<div class="p-4 sm:p-5 space-y-4">
			<!-- CPU Usage -->
			<div
				class="rounded-xl border border-gray-100 dark:border-gray-700/60 bg-gray-50/50 dark:bg-gray-900/30 p-4"
			>
				<div class="mb-2 flex items-center justify-between">
					<div class="flex items-center gap-2">
						<span
							class="flex h-7 w-7 items-center justify-center rounded-lg bg-blue-500/10 text-blue-600 dark:text-blue-400"
						>
							<Cpu class="h-4 w-4" />
						</span>
						<span class="text-sm font-semibold text-gray-900 dark:text-white">
							{$t('containers.cpu')}
						</span>
					</div>
					{#if currentStats}
						<span
							class="rounded-md bg-blue-500/10 px-2 py-0.5 font-mono text-sm font-semibold text-blue-600 dark:text-blue-400"
						>
							{currentStats.cpuPercentage.toFixed(2)}%
						</span>
					{/if}
				</div>
				<StreamingLineChart
					data={cpuChartData}
					lines={[{ key: 'cpu', label: 'CPU', color: '#3b82f6' }]}
					height={150}
					{windowMs}
					formatValue={(v) => `${v.toFixed(1)}%`}
					formatTooltipValue={(v) => `${v.toFixed(2)}%`}
				/>
			</div>

			<!-- Memory Usage -->
			<div
				class="rounded-xl border border-gray-100 dark:border-gray-700/60 bg-gray-50/50 dark:bg-gray-900/30 p-4"
			>
				<div class="mb-2 flex items-center justify-between">
					<div class="flex items-center gap-2">
						<span
							class="flex h-7 w-7 items-center justify-center rounded-lg bg-emerald-500/10 text-emerald-600 dark:text-emerald-400"
						>
							<MemoryStick class="h-4 w-4" />
						</span>
						<span class="text-sm font-semibold text-gray-900 dark:text-white">
							{$t('containers.ram')}
						</span>
					</div>
					{#if currentStats}
						<span
							class="rounded-md bg-emerald-500/10 px-2 py-0.5 font-mono text-sm font-semibold text-emerald-600 dark:text-emerald-400"
						>
							{formatBytes(currentStats.memoryUsage)} / {formatBytes(currentStats.memoryLimit)}
						</span>
					{/if}
				</div>
				<StreamingLineChart
					data={memoryChartData}
					lines={[{ key: 'memory', label: 'Memory', color: '#10b981' }]}
					height={150}
					{windowMs}
					formatValue={(v) => `${v.toFixed(1)} ${memoryUnit.unit}`}
					formatTooltipValue={(v) => `${v.toFixed(2)} ${memoryUnit.unit}`}
				/>
			</div>

			<!-- Network Usage -->
			<div
				class="rounded-xl border border-gray-100 dark:border-gray-700/60 bg-gray-50/50 dark:bg-gray-900/30 p-4"
			>
				<div class="mb-2 flex items-center justify-between gap-2">
					<div class="flex items-center gap-2">
						<span
							class="flex h-7 w-7 items-center justify-center rounded-lg bg-violet-500/10 text-violet-600 dark:text-violet-400"
						>
							<Network class="h-4 w-4" />
						</span>
						<span class="text-sm font-semibold text-gray-900 dark:text-white">
							{$t('containers.networkStats')}
						</span>
					</div>
					{#if currentStats && currentRates}
						<div class="flex items-center gap-1.5 font-mono text-xs">
							<span
								class="rounded-md bg-violet-500/10 px-1.5 py-0.5 font-semibold text-violet-600 dark:text-violet-400"
							>
								↓ {formatBytes(currentRates.networkRxRate)}/s
							</span>
							<span
								class="rounded-md bg-amber-500/10 px-1.5 py-0.5 font-semibold text-amber-600 dark:text-amber-400"
							>
								↑ {formatBytes(currentRates.networkTxRate)}/s
							</span>
						</div>
					{/if}
				</div>
				<StreamingLineChart
					data={networkChartData}
					lines={[
						{ key: 'rx', label: 'RX', color: '#8b5cf6' },
						{ key: 'tx', label: 'TX', color: '#f59e0b' }
					]}
					height={150}
					{windowMs}
					formatValue={(v) => `${v.toFixed(1)} ${networkRateUnit.unit}`}
					formatTooltipValue={(v) => `${v.toFixed(2)} ${networkRateUnit.unit}`}
				/>
			</div>

			<!-- Disk I/O -->
			<div
				class="rounded-xl border border-gray-100 dark:border-gray-700/60 bg-gray-50/50 dark:bg-gray-900/30 p-4"
			>
				<div class="mb-2 flex items-center justify-between gap-2">
					<div class="flex items-center gap-2">
						<span
							class="flex h-7 w-7 items-center justify-center rounded-lg bg-pink-500/10 text-pink-600 dark:text-pink-400"
						>
							<HardDrive class="h-4 w-4" />
						</span>
						<span class="text-sm font-semibold text-gray-900 dark:text-white">
							{$t('containers.diskStats')}
						</span>
					</div>
					{#if currentStats && currentRates}
						<div class="flex items-center gap-1.5 font-mono text-xs">
							<span
								class="rounded-md bg-violet-500/10 px-1.5 py-0.5 font-semibold text-violet-600 dark:text-violet-400"
							>
								R {formatBytes(currentRates.diskReadRate)}/s
							</span>
							<span
								class="rounded-md bg-pink-500/10 px-1.5 py-0.5 font-semibold text-pink-600 dark:text-pink-400"
							>
								W {formatBytes(currentRates.diskWriteRate)}/s
							</span>
						</div>
					{/if}
				</div>
				<StreamingLineChart
					data={diskChartData}
					lines={[
						{ key: 'read', label: 'Read', color: '#8b5cf6' },
						{ key: 'write', label: 'Write', color: '#ec4899' }
					]}
					height={150}
					{windowMs}
					formatValue={(v) => `${v.toFixed(1)} ${diskRateUnit.unit}`}
					formatTooltipValue={(v) => `${v.toFixed(2)} ${diskRateUnit.unit}`}
				/>
			</div>
		</div>
	</div>
{/if}
