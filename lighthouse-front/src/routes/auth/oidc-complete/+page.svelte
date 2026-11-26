<script lang="ts">
  // Landing page after a successful SSO login. The backend has already opened the
  // session and set the HttpOnly refresh cookie; exchange it for an access token.
  // First-login constraints (mustChangePassword / mustAddEmail) are deliberately not
  // enforced here: they only apply to local password logins.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import * as auth from '$lib/stores/auth.svelte';
  import { authApi } from '$lib/api';
  import { refreshAccessToken } from '$lib/api/tokenRefresh';
  import { safeReturnUrl } from '$lib/utils/oidc';
  import { t } from '$lib/i18n';

  onMount(async () => {
    const accessToken = await refreshAccessToken();
    if (!accessToken) {
      goto('/login?oidc_error=provider_error', { replaceState: true });
      return;
    }

    try {
      const user = await authApi.getCurrentUser();
      auth.login(accessToken, user);
      goto(safeReturnUrl(page.url.searchParams.get('returnUrl')), { replaceState: true });
    } catch {
      auth.logout();
      goto('/login?oidc_error=provider_error', { replaceState: true });
    }
  });
</script>

<div class="min-h-screen flex flex-col items-center justify-center gap-3 bg-linear-to-br from-gray-100 to-gray-200 dark:from-gray-900 dark:to-gray-800 text-sm text-gray-600 dark:text-gray-400">
  <span class="w-6 h-6 border-2 border-blue-600 border-t-transparent rounded-full animate-spin"></span>
  {$t('auth.oidc.completing')}
</div>
