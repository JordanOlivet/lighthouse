<script lang="ts">
  import Button from '$lib/components/ui/button.svelte';
  import CustomRegistryItem from './CustomRegistryItem.svelte';
  import AddRegistryDialog from './AddRegistryDialog.svelte';
  import { t } from '$lib/i18n';
  import { Plus, Server } from 'lucide-svelte';
  import type { ConfiguredRegistry, KnownRegistryInfo } from '$lib/types/registry';

  interface Props {
    registries: ConfiguredRegistry[];
    knownRegistryUrls: string[];
  }

  let { registries, knownRegistryUrls }: Props = $props();

  let showAddDialog = $state(false);

  // Filter out known registries to only show custom ones
  let customRegistries = $derived(
    registries.filter(r => !knownRegistryUrls.some(known =>
      r.registryUrl.toLowerCase().includes(known.toLowerCase()) ||
      known.toLowerCase().includes(r.registryUrl.toLowerCase())
    ))
  );
</script>

<!-- The parent card already titles this section, so only the action is shown here. -->
<div class="space-y-3 sm:space-y-4">
  <div class="flex justify-end">
    <Button size="sm" onclick={() => showAddDialog = true} class="w-full cursor-pointer sm:w-auto">
      <Plus class="w-4 h-4 mr-2" />
      {$t('settings.registry.addRegistry')}
    </Button>
  </div>

  {#if customRegistries.length === 0}
    <div class="text-center py-6 px-3 bg-gray-50 dark:bg-gray-900 rounded-lg sm:py-8">
      <Server class="w-10 h-10 mx-auto text-gray-400 mb-3 sm:w-12 sm:h-12 sm:mb-4" />
      <p class="text-sm text-gray-600 dark:text-gray-400 sm:text-base">{$t('settings.registry.noCustomRegistries')}</p>
      <p class="text-xs text-gray-500 dark:text-gray-500 mt-2 sm:text-sm">{$t('settings.registry.addCustomRegistryHint')}</p>
    </div>
  {:else}
    <div class="space-y-2 sm:space-y-3">
      {#each customRegistries as registry (registry.registryUrl)}
        <CustomRegistryItem {registry} />
      {/each}
    </div>
  {/if}
</div>

<AddRegistryDialog bind:open={showAddDialog} onclose={() => showAddDialog = false} />
