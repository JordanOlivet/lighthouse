<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import * as auth from '$lib/stores/auth.svelte';
  import { authApi, oidcApi } from '$lib/api';
  import type { OidcConfig } from '$lib/types';
  import { shouldAutoRedirect, safeReturnUrl, oidcLoginUrl } from '$lib/utils/oidc';
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

  // SSO (OIDC). null until the config is loaded, to avoid flashing the local form
  // right before an automatic redirect.
  const OIDC_ERROR_CODES = [
    'account_not_found',
    'account_disabled',
    'email_missing',
    'email_not_verified',
    'subject_mismatch',
    'provider_error'
  ];
  let oidc = $state<OidcConfig | null>(null);
  let redirecting = $state(false);
  let localFormExpanded = $state(false);

  const returnUrl = $derived(safeReturnUrl(page.url.searchParams.get('returnUrl')));
  const oidcError = $derived.by(() => {
    const code = page.url.searchParams.get('oidc_error');
    if (!code) return '';
    return OIDC_ERROR_CODES.includes(code) ? code : 'provider_error';
  });
  const ssoEnabled = $derived(oidc?.enabled ?? false);
  const ssoName = $derived(oidc?.displayName ?? 'SSO');
  // With auto-redirect on, the password form is a fallback: collapsed unless ?local=1.
  const localFormVisible = $derived(
    !ssoEnabled || !oidc?.autoRedirect || page.url.searchParams.has('local') || localFormExpanded
  );

  onMount(async () => {
    const config = await oidcApi.getOidcConfig();
    oidc = config;
    if (shouldAutoRedirect(config, page.url.searchParams)) {
      startSso();
    }
  });

  function startSso() {
    redirecting = true;
    window.location.href = oidcLoginUrl(returnUrl);
  }

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
        goto(returnUrl);
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
      {#if oidc === null || redirecting}
        <div class="flex flex-col items-center gap-3 py-6 text-sm text-gray-600 dark:text-gray-400">
          <span class="w-6 h-6 border-2 border-blue-600 border-t-transparent rounded-full animate-spin"></span>
          {#if redirecting}
            {$t('auth.oidc.redirecting', { name: ssoName })}
          {/if}
        </div>
      {:else}
      {#if successMessage}
        <div class="mb-4 p-3 bg-green-100 dark:bg-green-900/30 border border-green-400 dark:border-green-700 text-green-700 dark:text-green-300 rounded-lg text-sm">
          {successMessage}
        </div>
      {/if}

      {#if oidcError}
        <div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm">
          {$t(`auth.oidc.errors.${oidcError}`, {
            name: ssoName,
            email: page.url.searchParams.get('email') ?? ''
          })}
        </div>
      {/if}

      {#if ssoEnabled}
        <Button type="button" onclick={startSso} class="w-full cursor-pointer">
          {$t('auth.oidc.signInWith', { name: ssoName })}
        </Button>

        {#if localFormVisible}
          <div class="my-6 flex items-center gap-3 text-xs uppercase text-gray-400 dark:text-gray-500">
            <span class="h-px flex-1 bg-gray-200 dark:bg-gray-700"></span>
            {$t('auth.oidc.or')}
            <span class="h-px flex-1 bg-gray-200 dark:bg-gray-700"></span>
          </div>
        {:else}
          <div class="mt-4 text-center">
            <button
              type="button"
              onclick={() => (localFormExpanded = true)}
              class="text-sm text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 underline cursor-pointer"
            >
              {$t('auth.oidc.localLogin')}
            </button>
          </div>
        {/if}
      {/if}

      {#if localFormVisible}
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
      {/if}
      {/if}
    </CardContent>
  </Card>
</div>
