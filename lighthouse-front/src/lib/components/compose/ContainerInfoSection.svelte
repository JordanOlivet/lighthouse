<script lang="ts">
	import { Variable, HardDrive, Tag } from 'lucide-svelte';
	import { t } from '$lib/i18n';
	import type { ContainerDetails } from '$lib/types';

	interface Props {
		container: ContainerDetails;
	}

	let { container }: Props = $props();

	interface Section {
		id: string;
		title: string;
		count: number;
	}

	// Persistent open state - survives container updates
	let openState = $state<Record<string, boolean>>({
		env: true,
		ports: false,
		mounts: false,
		networks: false,
		labels: false
	});

	// Build sections reactively but don't reset open state
	const sections = $derived<Section[]>((() => {
		const result: Section[] = [];

		if (container.env && Object.keys(container.env).length > 0) {
			result.push({
				id: 'env',
			title: $t('containers.environment'),
				count: Object.keys(container.env).length
			});
		}

		if (container.portDetails && Object.keys(container.portDetails).length > 0) {
			result.push({
				id: 'ports',
			title: $t('containers.ports'),
				count: Object.keys(container.portDetails).length
			});
		}

		if (container.mounts && container.mounts.length > 0) {
			result.push({
				id: 'mounts',
			title: $t('containers.volumes'),
				count: container.mounts.length
			});
		}

		if (container.networks && container.networks.length > 0) {
			result.push({
				id: 'networks',
			title: $t('containers.networks'),
				count: container.networks.length
			});
		}

		if (container.labels && Object.keys(container.labels).length > 0) {
			result.push({
				id: 'labels',
			title: $t('containers.labels'),
				count: Object.keys(container.labels).length
			});
		}

		return result;
	})());

	function toggleSection(id: string) {
		openState[id] = !openState[id];
	}
</script>

<div
	class="bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg hover:shadow-2xl transition-all duration-300 overflow-hidden"
>
	<!-- Header -->
	<div class="px-4 py-2.5 sm:px-5 sm:py-3 border-b border-gray-200 dark:border-gray-700">
		<h3 class="text-sm font-semibold text-gray-900 dark:text-white sm:text-base">
			{$t('containers.technicalDetails')}
		</h3>
	</div>

	<!-- Content -->
	<div class="p-3 sm:p-5 space-y-2 sm:space-y-3">
		{#each sections as section}
			<div class="border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden">
				<!-- Section Header -->
				<button
					type="button"
					onclick={() => toggleSection(section.id)}
					class="flex items-center gap-2 w-full px-3 py-2 sm:px-4 sm:py-2.5 bg-gray-50 dark:bg-gray-700/50 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors text-left cursor-pointer"
				>
					<!-- Chevron -->
					<span class="inline-block transition-transform {openState[section.id] ? '' : '-rotate-90'}">
						<svg
							class="h-4 w-4 text-gray-600 dark:text-gray-400"
							viewBox="0 0 24 24"
							fill="none"
							stroke="currentColor"
							stroke-width="2"
							stroke-linecap="round"
							stroke-linejoin="round"
						>
							<path d="M6 9l6 6 6-6" />
						</svg>
					</span>

					<!-- Icon -->
					{#if section.id === 'env'}
						<Variable class="h-4 w-4 text-indigo-600 dark:text-indigo-400" />
					{:else if section.id === 'ports'}
						<Tag class="h-4 w-4 text-pink-600 dark:text-pink-400" />
					{:else if section.id === 'mounts'}
						<HardDrive class="h-4 w-4 text-orange-600 dark:text-orange-400" />
					{:else if section.id === 'networks'}
						<svg
							class="h-4 w-4 text-green-600 dark:text-green-400"
							fill="none"
							stroke="currentColor"
							viewBox="0 0 24 24"
						>
							<path
								stroke-linecap="round"
								stroke-linejoin="round"
								stroke-width="2"
								d="M21 12a9 9 0 01-9 9m9-9a9 9 0 00-9-9m9 9H3m9 9a9 9 0 01-9-9m9 9c1.657 0 3-4.03 3-9s-1.343-9-3-9m0 18c-1.657 0-3-4.03-3-9s1.343-9 3-9m-9 9a9 9 0 019-9"
							/>
						</svg>
					{:else if section.id === 'labels'}
						<Tag class="h-4 w-4 text-yellow-600 dark:text-yellow-400" />
					{/if}

					<span class="text-xs font-medium text-gray-800 dark:text-gray-100 flex items-center gap-2 sm:text-sm">
						{section.title}
						<span
							class="text-[10px] font-medium bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 px-1.5 py-0.5 rounded-full sm:text-xs sm:px-2"
						>
							{section.count}
						</span>
					</span>
				</button>

				<!-- Section Content -->
				{#if openState[section.id]}
					<div class="p-3 sm:p-4 bg-white dark:bg-gray-800">
						{#if section.id === 'env' && container.env}
							<div
								class="bg-gray-50 dark:bg-gray-900/50 rounded p-2 text-xs font-mono space-y-1 max-h-48 overflow-y-auto"
							>
								{#each Object.entries(container.env) as [key, value]}
									<div class="flex flex-wrap gap-x-2">
										<span class="break-all text-blue-600 dark:text-blue-400 font-semibold">{key}:</span>
										<span class="text-gray-700 dark:text-gray-300 break-all">{value}</span>
									</div>
								{/each}
							</div>
						{:else if section.id === 'ports' && container.portDetails}
							<div class="flex flex-wrap gap-1 text-xs font-mono">
								{#each Object.entries(container.portDetails) as [containerPort, hostPort]}
									<span
										class="inline-block bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300 px-2 py-1 rounded"
									>
										{hostPort} → {containerPort}
									</span>
								{/each}
							</div>
						{:else if section.id === 'mounts' && container.mounts}
							<ul class="text-xs font-mono space-y-1">
								{#each container.mounts as mount}
									<li class="break-all text-gray-700 dark:text-gray-300">
										{mount.source} : <span class="italic">{mount.destination}</span>
										{mount.readOnly ? $t('containers.readOnly') : ''}
									</li>
								{/each}
							</ul>
						{:else if section.id === 'networks' && container.networks}
							<ul class="text-xs font-mono flex flex-wrap gap-1">
								{#each container.networks as network}
									<li
										class="bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300 px-2 py-1 rounded"
									>
										{network}
									</li>
								{/each}
							</ul>
						{:else if section.id === 'labels' && container.labels}
							<div
								class="bg-gray-50 dark:bg-gray-900/50 rounded p-2 text-xs font-mono space-y-1 max-h-48 overflow-y-auto"
							>
								{#each Object.entries(container.labels) as [key, value]}
									<div class="flex flex-wrap gap-x-2">
										<span class="break-all text-blue-600 dark:text-blue-400">{key}:</span>
										<span class="break-all text-gray-700 dark:text-gray-300">{value}</span>
									</div>
								{/each}
							</div>
						{/if}
					</div>
				{/if}
			</div>
		{/each}
	</div>
</div>
