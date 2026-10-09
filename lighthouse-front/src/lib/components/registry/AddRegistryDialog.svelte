<script lang="ts">
  import { createMutation, useQueryClient } from '@tanstack/svelte-query';
  import Dialog from '$lib/components/ui/dialog.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import { registryApi } from '$lib/api/registry';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { Eye, EyeOff, LogIn } from 'lucide-svelte';

  interface Props {
    open: boolean;
    onclose: () => void;
  }

  let { open = $bindable(), onclose }: Props = $props();

  const queryClient = useQueryClient();

  let registryUrl = $state('');
  let authType = $state<'password' | 'token'>('password');
  let username = $state('');
  let password = $state('');
  let token = $state('');
  let showPassword = $state(false);

  const loginMutation = createMutation(() => ({
    mutationFn: () =>
      registryApi.login({
        registryUrl: registryUrl.trim(),
        authType,
        username: authType === 'password' ? username : (username || undefined),
        password: authType === 'password' ? password : undefined,
        token: authType === 'token' ? token : undefined,
      }),
    onSuccess: (result) => {
      if (result.success) {
        toast.success($t('settings.registry.loginSuccess'));
        queryClient.invalidateQueries({ queryKey: ['registry', 'configured'] });
        handleClose();
      } else {
        toast.error(result.error || $t('settings.registry.loginFailed'));
      }
    },
    onError: () => {
      toast.error($t('settings.registry.loginFailed'));
    },
  }));

  function handleClose() {
    open = false;
    resetForm();
    onclose();
  }

  function resetForm() {
    registryUrl = '';
    authType = 'password';
    username = '';
    password = '';
    token = '';
    showPassword = false;
  }

  function handleSubmit(e: Event) {
    e.preventDefault();
    if (!registryUrl.trim()) {
      toast.error($t('settings.registry.urlRequired'));
      return;
    }
    loginMutation.mutate();
  }
</script>

<Dialog {open} onclose={handleClose}>
  <div class="p-4 sm:p-6">
    <h2 class="text-lg font-semibold text-gray-900 dark:text-white mb-4 sm:text-xl">
      {$t('settings.registry.addCustomRegistry')}
    </h2>

    <form onsubmit={handleSubmit} class="space-y-4">
      <div>
        <label for="registry-url" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
          {$t('settings.registry.registryUrl')}
        </label>
        <input
          id="registry-url"
          type="text"
          bind:value={registryUrl}
          placeholder="registry.example.com"
          required
          class="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
        />
      </div>

      <fieldset>
        <legend class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-2">
          {$t('settings.registry.authType')}
        </legend>
        <div class="flex flex-col gap-2 sm:flex-row sm:gap-4">
          <label class="flex items-center gap-2 cursor-pointer">
            <input
              type="radio"
              name="addAuthType"
              value="password"
              bind:group={authType}
              class="w-4 h-4 text-blue-600"
            />
            <span class="text-sm text-gray-700 dark:text-gray-300">
              {$t('settings.registry.usernamePassword')}
            </span>
          </label>
          <label class="flex items-center gap-2 cursor-pointer">
            <input
              type="radio"
              name="addAuthType"
              value="token"
              bind:group={authType}
              class="w-4 h-4 text-blue-600"
            />
            <span class="text-sm text-gray-700 dark:text-gray-300">
              {$t('settings.registry.accessToken')}
            </span>
          </label>
        </div>
      </fieldset>

      {#if authType === 'password'}
        <div>
          <label for="add-username" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            {$t('auth.username')}
          </label>
          <input
            id="add-username"
            type="text"
            bind:value={username}
            placeholder={$t('users.usernamePlaceholder')}
            required
            class="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
          />
        </div>

        <div>
          <label for="add-password" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            {$t('auth.password')}
          </label>
          <div class="relative">
            <input
              id="add-password"
              type={showPassword ? 'text' : 'password'}
              bind:value={password}
              required
              class="w-full px-3 py-2 pr-10 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
            />
            <button
              type="button"
              onclick={() => showPassword = !showPassword}
              class="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-gray-500 hover:text-gray-700 dark:hover:text-gray-300 cursor-pointer"
            >
              {#if showPassword}
                <EyeOff class="w-4 h-4" />
              {:else}
                <Eye class="w-4 h-4" />
              {/if}
            </button>
          </div>
        </div>
      {:else}
        <div>
          <label for="add-token-username" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            {$t('auth.username')} <span class="text-gray-400">({$t('common.optional')})</span>
          </label>
          <input
            id="add-token-username"
            type="text"
            bind:value={username}
            placeholder={$t('settings.registry.tokenUsernamePlaceholder')}
            class="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
          />
        </div>

        <div>
          <label for="add-token" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            {$t('settings.registry.accessToken')}
          </label>
          <div class="relative">
            <input
              id="add-token"
              type={showPassword ? 'text' : 'password'}
              bind:value={token}
              required
              class="w-full px-3 py-2 pr-10 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
            />
            <button
              type="button"
              onclick={() => showPassword = !showPassword}
              class="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-gray-500 hover:text-gray-700 dark:hover:text-gray-300 cursor-pointer"
            >
              {#if showPassword}
                <EyeOff class="w-4 h-4" />
              {:else}
                <Eye class="w-4 h-4" />
              {/if}
            </button>
          </div>
        </div>
      {/if}

      <div class="flex justify-end gap-3 pt-4">
        <Button type="button" variant="outline" onclick={handleClose} disabled={loginMutation.isPending} class="cursor-pointer">
          {$t('common.cancel')}
        </Button>
        <Button type="submit" disabled={loginMutation.isPending} class="cursor-pointer">
          {#if loginMutation.isPending}
            <span class="animate-spin mr-2">⏳</span>
            {$t('common.loading')}
          {:else}
            <LogIn class="w-4 h-4 mr-2" />
            {$t('common.save')}
          {/if}
        </Button>
      </div>
    </form>
  </div>
</Dialog>
