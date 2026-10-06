<script lang="ts">
  import Button from '$lib/components/ui/button.svelte';
  import { Eye, EyeOff, LogIn } from 'lucide-svelte';
  import { t } from '$lib/i18n';

  interface Props {
    registryUrl: string;
    authType?: 'password' | 'token';
    isLoading?: boolean;
    onsubmit: (data: { username?: string; password?: string; token?: string; authType: 'password' | 'token' }) => void;
    oncancel?: () => void;
  }

  let { registryUrl, authType: initialAuthType = 'password', isLoading = false, onsubmit, oncancel }: Props = $props();

  let authType = $state<'password' | 'token'>(initialAuthType);
  let username = $state('');
  let password = $state('');
  let token = $state('');
  let showPassword = $state(false);

  function handleSubmit(e: Event) {
    e.preventDefault();
    if (authType === 'password') {
      onsubmit({ username, password, authType });
    } else {
      onsubmit({ username: username || undefined, token, authType });
    }
  }

  function resetForm() {
    username = '';
    password = '';
    token = '';
  }
</script>

<form onsubmit={handleSubmit} class="space-y-4">
  <div>
    <span class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
      {$t('settings.registry.registryUrl')}
    </span>
    <p class="text-sm text-gray-600 dark:text-gray-400 font-mono bg-gray-100 dark:bg-gray-800 px-3 py-2 rounded-lg">
      {registryUrl}
    </p>
  </div>

  <fieldset>
    <legend class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-2">
      {$t('settings.registry.authType')}
    </legend>
    <div class="flex flex-col gap-2 sm:flex-row sm:gap-4">
      <label class="flex items-center gap-2 cursor-pointer">
        <input
          type="radio"
          name="authType"
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
          name="authType"
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
      <label for="username" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
        {$t('auth.username')}
      </label>
      <input
        id="username"
        type="text"
        bind:value={username}
        placeholder={$t('users.usernamePlaceholder')}
        required
        class="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
      />
    </div>

    <div>
      <label for="password" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
        {$t('auth.password')}
      </label>
      <div class="relative">
        <input
          id="password"
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
      <label for="token-username" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
        {$t('auth.username')} <span class="text-gray-400">({$t('common.optional')})</span>
      </label>
      <input
        id="token-username"
        type="text"
        bind:value={username}
        placeholder={$t('settings.registry.tokenUsernamePlaceholder')}
        class="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-transparent"
      />
      <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
        {$t('settings.registry.tokenUsernameHint')}
      </p>
    </div>

    <div>
      <label for="token" class="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
        {$t('settings.registry.accessToken')}
      </label>
      <div class="relative">
        <input
          id="token"
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
    {#if oncancel}
      <Button type="button" variant="outline" onclick={oncancel} disabled={isLoading} class="cursor-pointer">
        {$t('common.cancel')}
      </Button>
    {/if}
    <Button type="submit" disabled={isLoading} class="cursor-pointer">
      {#if isLoading}
        <span class="animate-spin mr-2">⏳</span>
        {$t('common.loading')}
      {:else}
        <LogIn class="w-4 h-4 mr-2" />
        {$t('auth.login')}
      {/if}
    </Button>
  </div>
</form>
