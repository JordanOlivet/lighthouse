<script lang="ts">
  import { createMutation, useQueryClient } from '@tanstack/svelte-query';
  import Button from '$lib/components/ui/button.svelte';
  import Badge from '$lib/components/ui/badge.svelte';
  import { registryApi } from '$lib/api/registry';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { RefreshCw, Trash2, CheckCircle, Key } from 'lucide-svelte';
  import type { ConfiguredRegistry } from '$lib/types/registry';

  interface Props {
    registry: ConfiguredRegistry;
  }

  let { registry }: Props = $props();

  const queryClient = useQueryClient();

  let isTestingConnection = $state(false);
  let connectionTestResult = $state<{ success: boolean; authenticated: boolean } | null>(null);

  const logoutMutation = createMutation(() => ({
    mutationFn: () => registryApi.logout(registry.registryUrl),
    onSuccess: (result) => {
      if (result.success) {
        toast.success($t('settings.registry.logoutSuccess'));
        queryClient.invalidateQueries({ queryKey: ['registry', 'configured'] });
        connectionTestResult = null;
      } else {
        toast.error(result.message || $t('settings.registry.logoutFailed'));
      }
    },
    onError: () => {
      toast.error($t('settings.registry.logoutFailed'));
    },
  }));

  async function handleTestConnection() {
    isTestingConnection = true;
    connectionTestResult = null;
    try {
      const result = await registryApi.testConnection(registry.registryUrl);
      connectionTestResult = { success: result.success, authenticated: result.isAuthenticated };
      if (result.success) {
        toast.success($t('settings.registry.connectionSuccess'));
      } else {
        toast.error(result.error || $t('settings.registry.connectionFailed'));
      }
    } catch {
      toast.error($t('settings.registry.connectionFailed'));
      connectionTestResult = { success: false, authenticated: false };
    } finally {
      isTestingConnection = false;
    }
  }
</script>

<div class="flex items-center justify-between gap-2 p-3 bg-gray-50 dark:bg-gray-900 rounded-lg sm:p-4">
  <div class="flex items-center gap-4 flex-1 min-w-0">
    <div class="min-w-0 flex-1">
      <p class="font-mono text-xs text-gray-900 dark:text-white truncate sm:text-sm" title={registry.registryUrl}>
        {registry.registryUrl}
      </p>
      <div class="flex items-center gap-2 mt-1 flex-wrap">
        {#if registry.username}
          <span class="text-xs text-gray-500 dark:text-gray-400">
            {registry.username}
          </span>
        {/if}
        {#if registry.usesCredentialHelper}
          <Badge variant="outline" class="text-xs">
            <Key class="w-3 h-3 mr-1" />
            {registry.credentialHelperName || 'cred helper'}
          </Badge>
        {/if}
        {#if connectionTestResult}
          <Badge variant={connectionTestResult.success ? 'success' : 'destructive'} class="text-xs">
            {connectionTestResult.success ? 'OK' : 'Failed'}
          </Badge>
        {/if}
      </div>
    </div>
  </div>

  <div class="flex shrink-0 items-center gap-1 sm:gap-2">
    <Button
      size="sm"
      variant="ghost"
      onclick={handleTestConnection}
      disabled={isTestingConnection}
      title={$t('settings.registry.testConnection')}
      aria-label={$t('settings.registry.testConnection')}
      class="cursor-pointer"
    >
      {#if isTestingConnection}
        <RefreshCw class="w-4 h-4 animate-spin" />
      {:else}
        <RefreshCw class="w-4 h-4" />
      {/if}
    </Button>
    <Button
      size="sm"
      variant="ghost"
      onclick={() => logoutMutation.mutate()}
      disabled={logoutMutation.isPending}
      title={$t('settings.registry.remove')}
      aria-label={$t('settings.registry.remove')}
      class="text-red-600 hover:text-red-700 hover:bg-red-100 dark:hover:bg-red-900/30 cursor-pointer"
    >
      <Trash2 class="w-4 h-4" />
    </Button>
  </div>
</div>
