<script lang="ts">
  import { goto } from '$app/navigation';
  import { authApi } from '$lib/api';
  import { t } from '$lib/i18n';
  import * as auth from '$lib/stores/auth.svelte';
  import PasswordInput from '$lib/components/common/PasswordInput.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Card from '$lib/components/ui/card.svelte';
  import CardHeader from '$lib/components/ui/card-header.svelte';
  import CardTitle from '$lib/components/ui/card-title.svelte';
  import CardContent from '$lib/components/ui/card-content.svelte';
  import { toast } from 'svelte-sonner';
  import { KeyRound } from 'lucide-svelte';
  import { validatePassword, type PasswordValidationError } from '$lib/utils/passwordValidation';

  let currentPassword = $state('');
  let newPassword = $state('');
  let confirmPassword = $state('');
  let error = $state('');
  let validationErrors = $state<PasswordValidationError[]>([]);
  let loading = $state(false);

  // OIDC-only accounts have no local password to change.
  $effect(() => {
    if (auth.auth.user && auth.auth.user.hasPassword === false) {
      goto('/dashboard', { replaceState: true });
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    error = '';
    validationErrors = [];

    if (newPassword !== confirmPassword) {
      error = $t('auth.passwordMismatch');
      return;
    }

    const validation = validatePassword(newPassword);
    if (!validation.isValid) {
      validationErrors = validation.errors;
      return;
    }

    loading = true;
    try {
      const response = await authApi.changePassword(currentPassword, newPassword);
      toast.success($t('auth.passwordChanged'));

      // Store the new access token only; the rotated refresh token is set as an
      // HttpOnly cookie by the backend.
      if (typeof localStorage !== 'undefined') {
        localStorage.setItem('accessToken', response.accessToken);
      }

      // Fetch updated user data
      const user = await authApi.getCurrentUser();

      // Update auth store with new token and user data
      auth.login(response.accessToken, user);

      goto('/dashboard');
    } catch (err: any) {
      // Extract detailed validation errors if available
      const responseData = err.response?.data;
      if (responseData?.errors && typeof responseData.errors === 'object') {
        const errorMessages = Object.values(responseData.errors)
          .flat()
          .filter((msg): msg is string => typeof msg === 'string');
        error = errorMessages.length > 0 ? errorMessages.join('. ') : (responseData.message || $t('errors.generic'));
      } else {
        error = responseData?.message || $t('errors.generic');
      }
    } finally {
      loading = false;
    }
  }
</script>

<div class="min-h-screen flex items-center justify-center bg-linear-to-br from-gray-100 to-gray-200 dark:from-gray-900 dark:to-gray-800">
  <Card class="max-w-md w-full mx-4 shadow-xl">
    <CardHeader class="text-center pb-2">
      <div class="mx-auto w-16 h-16 bg-linear-to-br from-amber-500 to-orange-600 rounded-xl flex items-center justify-center shadow-lg mb-4">
        <KeyRound class="w-8 h-8 text-white" />
      </div>
      <CardTitle class="text-2xl font-bold">{$t('auth.changePassword')}</CardTitle>
      <p class="text-sm text-gray-500 dark:text-gray-400 mt-2">{$t('auth.changePasswordSubtitle')}</p>
    </CardHeader>
    <CardContent class="pt-6">
      {#if error}
        <div class="mb-4 p-3 bg-red-100 dark:bg-red-900/30 border border-red-400 dark:border-red-700 text-red-700 dark:text-red-300 rounded-lg text-sm">
          {error}
        </div>
      {/if}

      {#if validationErrors.length > 0}
        <div class="mb-4 p-3 bg-yellow-100 dark:bg-yellow-900/30 border border-yellow-400 dark:border-yellow-700 rounded-lg">
          <h3 class="text-sm font-semibold text-yellow-800 dark:text-yellow-300">{$t('auth.passwordRequirements')}:</h3>
          <ul class="mt-2 list-disc pl-5 text-sm text-yellow-700 dark:text-yellow-400 space-y-1">
            {#each validationErrors as err}
              <li>{$t(err.key, err.params)}</li>
            {/each}
          </ul>
        </div>
      {/if}

      <form onsubmit={handleSubmit}>
        <div class="mb-4">
          <label for="currentPassword" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
            {$t('auth.currentPassword')}
          </label>
          <PasswordInput
            id="currentPassword"
            bind:value={currentPassword}
            placeholder={$t('auth.currentPasswordPlaceholder')}
            required
          />
        </div>

        <div class="mb-4">
          <label for="newPassword" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
            {$t('auth.newPassword')}
          </label>
          <PasswordInput
            id="newPassword"
            bind:value={newPassword}
            placeholder={$t('auth.newPasswordPlaceholder')}
            required
          />
        </div>

        <div class="mb-6">
          <label for="confirmPassword" class="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-2">
            {$t('auth.confirmPassword')}
          </label>
          <PasswordInput
            id="confirmPassword"
            bind:value={confirmPassword}
            placeholder={$t('auth.confirmPasswordPlaceholder')}
            required
          />
        </div>

        <Button type="submit" disabled={loading} class="w-full">
          {#if loading}
            <span class="flex items-center justify-center gap-2">
              <span class="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin"></span>
              {$t('common.loading')}
            </span>
          {:else}
            {$t('auth.changePassword')}
          {/if}
        </Button>
      </form>
    </CardContent>
  </Card>
</div>
