<script lang="ts">
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import Button from '$lib/components/ui/button.svelte';
  import Badge from '$lib/components/ui/badge.svelte';
  import Dialog from '$lib/components/ui/dialog.svelte';
  import RegistryLoginForm from './RegistryLoginForm.svelte';
  import { registryApi } from '$lib/api/registry';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { RefreshCw, LogOut, Settings, CheckCircle, XCircle } from 'lucide-svelte';
  import type { KnownRegistryInfo, ConfiguredRegistry } from '$lib/types/registry';

  interface Props {
    registry: KnownRegistryInfo;
    configuredRegistry?: ConfiguredRegistry | null;
  }

  let { registry, configuredRegistry }: Props = $props();

  const queryClient = useQueryClient();

  let showLoginDialog = $state(false);
  let isTestingConnection = $state(false);
  let connectionTestResult = $state<{ success: boolean; authenticated: boolean } | null>(null);

  const loginMutation = createMutation(() => ({
    mutationFn: (data: { username?: string; password?: string; token?: string; authType: 'password' | 'token' }) =>
      registryApi.login({
        registryUrl: registry.registryUrl,
        authType: data.authType,
        username: data.username,
        password: data.password,
        token: data.token,
      }),
    onSuccess: (result) => {
      if (result.success) {
        toast.success($t('settings.registry.loginSuccess'));
        showLoginDialog = false;
        queryClient.invalidateQueries({ queryKey: ['registry', 'configured'] });
      } else {
        toast.error(result.error || $t('settings.registry.loginFailed'));
      }
    },
    onError: () => {
      toast.error($t('settings.registry.loginFailed'));
    },
  }));

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

  function handleLoginSubmit(data: { username?: string; password?: string; token?: string; authType: 'password' | 'token' }) {
    loginMutation.mutate(data);
  }

  let isConfigured = $derived(configuredRegistry?.isConfigured ?? false);
</script>

<div class="p-3 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg sm:p-4">
  <div class="flex flex-wrap items-start justify-between gap-2">
    <div class="flex min-w-0 items-center gap-2.5 sm:gap-3">
      {#if registry.icon === 'docker'}
        <div class="w-9 h-9 shrink-0 bg-blue-100 dark:bg-blue-900 rounded-lg flex items-center justify-center sm:w-10 sm:h-10">
          <svg class="w-5 h-5 sm:w-6 sm:h-6 text-blue-600 dark:text-blue-400" viewBox="0 0 24 24" fill="currentColor">
            <path d="M13.983 11.078h2.119a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.119a.185.185 0 00-.185.185v1.888c0 .102.083.185.185.185m-2.954-5.43h2.118a.186.186 0 00.186-.186V3.574a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.186m0 2.716h2.118a.187.187 0 00.186-.186V6.29a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.887c0 .102.082.185.185.186m-2.93 0h2.12a.186.186 0 00.184-.186V6.29a.185.185 0 00-.185-.185H8.1a.185.185 0 00-.185.185v1.887c0 .102.083.185.185.186m-2.964 0h2.119a.186.186 0 00.185-.186V6.29a.185.185 0 00-.185-.185H5.136a.186.186 0 00-.186.185v1.887c0 .102.084.185.186.186m5.893 2.715h2.118a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m-2.93 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.083.185.185.185m-2.964 0h2.119a.185.185 0 00.185-.185V9.006a.185.185 0 00-.185-.186h-2.12a.186.186 0 00-.185.186v1.887c0 .102.084.185.186.185m-2.92 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.082.185.185.185M23.763 9.89c-.065-.051-.672-.51-1.954-.51-.338.001-.676.03-1.01.087-.248-1.7-1.653-2.53-1.716-2.566l-.344-.199-.226.327c-.284.438-.49.922-.612 1.43-.23.97-.09 1.882.403 2.661-.595.332-1.55.413-1.744.42H.751a.751.751 0 00-.75.748 11.376 11.376 0 00.692 4.062c.545 1.428 1.355 2.48 2.41 3.124 1.18.723 3.1 1.137 5.275 1.137.983.003 1.963-.086 2.93-.266a12.248 12.248 0 003.823-1.389c.98-.567 1.86-1.288 2.61-2.136 1.252-1.418 1.998-2.997 2.553-4.4h.221c1.372 0 2.215-.549 2.68-1.009.309-.293.55-.65.707-1.046l.098-.288z"/>
          </svg>
        </div>
      {:else if registry.icon === 'github'}
        <div class="w-9 h-9 shrink-0 bg-gray-100 dark:bg-gray-700 rounded-lg flex items-center justify-center sm:w-10 sm:h-10">
          <svg class="w-5 h-5 sm:w-6 sm:h-6 text-gray-800 dark:text-gray-200" viewBox="0 0 24 24" fill="currentColor">
            <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/>
          </svg>
        </div>
      {:else}
        <div class="w-9 h-9 shrink-0 bg-gray-100 dark:bg-gray-700 rounded-lg flex items-center justify-center sm:w-10 sm:h-10">
          <Settings class="w-5 h-5 text-gray-600 dark:text-gray-400 sm:w-6 sm:h-6" />
        </div>
      {/if}

      <div class="min-w-0">
        <h3 class="text-sm font-semibold text-gray-900 dark:text-white sm:text-base">{registry.name}</h3>
        <p class="break-all text-xs text-gray-500 dark:text-gray-400 font-mono sm:text-sm">{registry.registryUrl}</p>
        {#if isConfigured && configuredRegistry?.username}
          <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
            {$t('settings.registry.loggedInAs')}: <span class="font-medium">{configuredRegistry.username}</span>
          </p>
        {/if}
      </div>
    </div>

    <div class="flex items-center gap-2">
      {#if isConfigured}
        <Badge variant="success">
          <CheckCircle class="w-3 h-3 mr-1" />
          {$t('settings.registry.configured')}
        </Badge>
      {:else}
        <Badge variant="secondary">
          <XCircle class="w-3 h-3 mr-1" />
          {$t('settings.registry.notConfigured')}
        </Badge>
      {/if}
    </div>
  </div>

  {#if connectionTestResult}
    <div class="mt-3 p-2 rounded-lg {connectionTestResult.success ? 'bg-green-50 dark:bg-green-900/20' : 'bg-red-50 dark:bg-red-900/20'}">
      <p class="text-xs sm:text-sm {connectionTestResult.success ? 'text-green-700 dark:text-green-400' : 'text-red-700 dark:text-red-400'}">
        {#if connectionTestResult.success}
          {connectionTestResult.authenticated ? $t('settings.registry.authenticatedConnection') : $t('settings.registry.connectionReachable')}
        {:else}
          {$t('settings.registry.connectionFailed')}
        {/if}
      </p>
    </div>
  {/if}

  <div class="mt-3 flex flex-wrap gap-2 sm:mt-4">
    {#if isConfigured}
      <Button
        size="sm"
        variant="outline"
        onclick={handleTestConnection}
        disabled={isTestingConnection}
        class="flex-1 cursor-pointer sm:flex-none"
      >
        {#if isTestingConnection}
          <RefreshCw class="w-4 h-4 mr-2 animate-spin" />
        {:else}
          <RefreshCw class="w-4 h-4 mr-2" />
        {/if}
        {$t('settings.registry.testConnection')}
      </Button>
      <Button
        size="sm"
        variant="outline"
        onclick={() => logoutMutation.mutate()}
        disabled={logoutMutation.isPending}
        class="flex-1 cursor-pointer sm:flex-none"
      >
        <LogOut class="w-4 h-4 mr-2" />
        {$t('auth.logout')}
      </Button>
    {:else}
      <Button size="sm" onclick={() => showLoginDialog = true} class="flex-1 cursor-pointer sm:flex-none">
        <Settings class="w-4 h-4 mr-2" />
        {$t('settings.registry.configure')}
      </Button>
    {/if}
  </div>
</div>

<Dialog open={showLoginDialog} onclose={() => showLoginDialog = false}>
  <div class="p-4 sm:p-6">
    <h2 class="text-lg font-semibold text-gray-900 dark:text-white mb-4 sm:text-xl">
      {$t('settings.registry.loginTo')} {registry.name}
    </h2>
    <RegistryLoginForm
      registryUrl={registry.registryUrl}
      isLoading={loginMutation.isPending}
      onsubmit={handleLoginSubmit}
      oncancel={() => showLoginDialog = false}
    />
  </div>
</Dialog>
