<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import * as auth from '$lib/stores/auth.svelte';
  import { authApi } from '$lib/api';
  import { t } from '$lib/i18n';
  import Input from '$lib/components/ui/input.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Card from '$lib/components/ui/card.svelte';
  import CardHeader from '$lib/components/ui/card-header.svelte';
  import CardTitle from '$lib/components/ui/card-title.svelte';
  import CardContent from '$lib/components/ui/card-content.svelte';
  import PasswordInput from '$lib/components/common/PasswordInput.svelte';

  let username = $state('');
  let password = $state('');
  let rememberMe = $state(false);
  let error = $state('');
  let loading = $state(false);
  let successMessage = $state('');

  // Check for reset=success URL parameter
  $effect(() => {
    if (page.url.searchParams.get('reset') === 'success') {
      successMessage = 'Password reset successful! You can now log in with your new password.';
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    error = '';
    loading = true;

    try {
      const response = await authApi.login({ username, password, rememberMe });

      // Store the access token only. The refresh token is set by the backend as an
      // HttpOnly cookie and is never exposed to JavaScript.
      if (typeof localStorage !== 'undefined') {
        localStorage.setItem('accessToken', response.accessToken);
      }

      // Fetch complete user data
      const user = await authApi.getCurrentUser();

      // Update auth store
      auth.login(response.accessToken, user);

      if (response.mustAddEmail) {
        goto('/add-email');
      } else if (response.mustChangePassword) {
        goto('/change-password');
      } else {
        goto('/dashboard');
      }
    } catch (err: any) {
      if (typeof localStorage !== 'undefined') {
        localStorage.removeItem('accessToken');
      }
      // Extract detailed validation errors if available
      const responseData = err.response?.data;
      if (responseData?.errors && typeof responseData.errors === 'object') {
        const errorMessages = Object.values(responseData.errors)
          .flat()
          .filter((msg): msg is string => typeof msg === 'string');
        error = errorMessages.length > 0 ? errorMessages.join('. ') : (responseData.message || $t('auth.loginFailed'));
      } else {
        error = responseData?.message || $t('auth.loginFailed');
      }
    } finally {
      loading = false;
    }
  }
</script>

<div class="min-h-screen flex items-center justify-center bg-linear-to-br from-gray-100 to-gray-200 dark:from-gray-900 dark:to-gray-800">
  <Card class="max-w-md w-full mx-4 shadow-xl">
    <CardHeader class="text-center pb-2">
      <img src="/lighthouse-logo.svg" alt="Lighthouse" class="mx-auto w-16 h-16 rounded-xl shadow-lg mb-4" />
      <CardTitle class="text-2xl font-bold">{$t('app.title')}</CardTitle>
    </CardHeader>
    <CardContent class="pt-6">
      {#if successMessage}
        <div class="mb-4 p-3 bg-green-100 dark:bg-green-900/30 border border-green-400 dark:border-green-700 text-green-700 dark:text-green-300 rounded-lg text-sm">
          {successMessage}
        </div>
      {/if}

      {#if error}
        <div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm">
          {error}
        </div>
      {/if}

      <form onsubmit={handleSubmit}>
        <div class="mb-4">
          <label for="username" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
            {$t('auth.username')}
          </label>
          <Input
            type="text"
            id="username"
            bind:value={username}
            required
            autocomplete="username"
          />
        </div>

        <div class="mb-4">
          <div class="flex items-center justify-between mb-2">
            <label for="password" class="block text-sm font-medium text-gray-700 dark:text-gray-200">
              {$t('auth.password')}
            </label>
            <a href="/forgot-password" class="text-sm text-blue-600 hover:text-blue-500 dark:text-blue-400 dark:hover:text-blue-300">
              Forgot password?
            </a>
          </div>
          <PasswordInput
            id="password"
            bind:value={password}
            required
            autocomplete="current-password"
          />
        </div>

        <div class="mb-6">
          <label class="flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              bind:checked={rememberMe}
              class="w-4 h-4 text-blue-600 bg-gray-100 border-gray-300 rounded focus:ring-blue-500 dark:focus:ring-blue-600 dark:ring-offset-gray-800 focus:ring-2 dark:bg-gray-700 dark:border-gray-600"
            />
            <span class="text-sm text-gray-700 dark:text-gray-300">
              {$t('auth.rememberMe')}
              <span class="text-gray-500 dark:text-gray-400">(30 {$t('common.days')})</span>
            </span>
          </label>
        </div>

        <Button type="submit" disabled={loading} class="w-full cursor-pointer">
          {#if loading}
            <span class="flex items-center justify-center gap-2">
              <span class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></span>
              {$t('auth.loggingIn')}
            </span>
          {:else}
            {$t('auth.login')}
          {/if}
        </Button>
      </form>

      <div class="mt-6 text-center text-sm text-gray-600 dark:text-gray-400">
        <p>{$t('auth.defaultCredentials')}</p>
        <p class="font-mono bg-gray-100 dark:bg-gray-800 p-2 mt-2 rounded text-gray-800 dark:text-gray-200">admin / adminadmin</p>
      </div>
    </CardContent>
  </Card>
</div>
