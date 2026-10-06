<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import Card from '$lib/components/ui/card.svelte';
  import CardHeader from '$lib/components/ui/card-header.svelte';
  import CardTitle from '$lib/components/ui/card-title.svelte';
  import CardContent from '$lib/components/ui/card-content.svelte';
  import LoadingState from '$lib/components/common/LoadingState.svelte';
  import KnownRegistryCard from './KnownRegistryCard.svelte';
  import CustomRegistryList from './CustomRegistryList.svelte';
  import { registryApi } from '$lib/api/registry';
  import { t } from '$lib/i18n';
  import { AlertCircle } from 'lucide-svelte';

  // Query for configured registries
  // Refetch each time the tab is accessed (staleTime: 0)
  // No polling interval - only refetch on mount or manual invalidation
  const configuredQuery = createQuery(() => ({
    queryKey: ['registry', 'configured'],
    queryFn: () => registryApi.getConfiguredRegistries(),
    staleTime: 0, // Always refetch when component mounts
    refetchOnWindowFocus: false, // Don't refetch on window focus
  }));

  // Query for known registries (static data, cache indefinitely)
  const knownQuery = createQuery(() => ({
    queryKey: ['registry', 'known'],
    queryFn: () => registryApi.getKnownRegistries(),
    staleTime: Infinity, // Never consider stale (static list)
  }));

  // Find configured info for a known registry
  function findConfiguredRegistry(registryUrl: string) {
    if (!configuredQuery.data) return null;
    return configuredQuery.data.find(r =>
      r.registryUrl.toLowerCase().includes(registryUrl.toLowerCase()) ||
      registryUrl.toLowerCase().includes(r.registryUrl.toLowerCase())
    ) || null;
  }

  let knownRegistryUrls = $derived(knownQuery.data?.map(r => r.registryUrl) || []);
</script>

<div class="space-y-4 sm:space-y-6">
  <!-- Known Registries Section -->
  <Card>
    <CardHeader>
      <CardTitle>{$t('settings.registry.knownRegistries')}</CardTitle>
    </CardHeader>
    <CardContent>
      {#if knownQuery.isLoading}
        <LoadingState message={$t('common.loading')} />
      {:else if knownQuery.error}
        <div class="text-center py-8 text-red-500">
          <AlertCircle class="w-12 h-12 mx-auto mb-4" />
          <p>{$t('errors.generic')}</p>
        </div>
      {:else if knownQuery.data}
        <div class="space-y-3 sm:space-y-4">
          {#each knownQuery.data as registry (registry.registryUrl)}
            <KnownRegistryCard
              {registry}
              configuredRegistry={findConfiguredRegistry(registry.registryUrl)}
            />
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>

  <!-- Custom Registries Section -->
  <Card>
    <CardHeader>
      <CardTitle>{$t('settings.registry.customRegistries')}</CardTitle>
    </CardHeader>
    <CardContent>
      {#if configuredQuery.isLoading}
        <LoadingState message={$t('common.loading')} />
      {:else if configuredQuery.error}
        <div class="text-center py-8 text-red-500">
          <AlertCircle class="w-12 h-12 mx-auto mb-4" />
          <p>{$t('errors.generic')}</p>
        </div>
      {:else}
        <CustomRegistryList
          registries={configuredQuery.data || []}
          {knownRegistryUrls}
        />
      {/if}
    </CardContent>
  </Card>
</div>
