<script lang="ts">
  import { ChevronDown, Globe, Sun, Moon, LogOut } from 'lucide-svelte';
  import { goto } from '$app/navigation';
  import * as auth from '$lib/stores/auth.svelte';
  import * as theme from '$lib/stores/theme.svelte';
  import { authApi } from '$lib/api';
  import { logger } from '$lib/utils/logger';
  import { locale, availableLocales, setLocale, type Locale } from '$lib/i18n';
  import { t } from '$lib/i18n';

  let isOpen = $state(false);
  let menuRef: HTMLDivElement | null = $state(null);

  const currentLanguage = $derived(
    availableLocales.find(lang => lang.code === $locale) || availableLocales[0]
  );

  function handleClickOutside(event: MouseEvent) {
    if (menuRef && !menuRef.contains(event.target as Node)) {
      isOpen = false;
    }
  }

  function selectLanguage(localeCode: Locale) {
    setLocale(localeCode);
  }

  async function handleLogout() {
    isOpen = false;
    try {
      // The refresh token is sent via the HttpOnly cookie; the backend revokes the
      // session and clears the cookie.
      await authApi.logout();
    } catch (error) {
      logger.error('Logout error:', error);
    } finally {
      auth.logout();
      // logged_out=1 stops /login from immediately redirecting back to the SSO provider.
      goto('/login?logged_out=1');
    }
  }
</script>

<svelte:window onclick={handleClickOutside} />

<div class="relative" bind:this={menuRef}>
  <!-- Trigger button -->
  <button
    onclick={() => isOpen = !isOpen}
    class="flex items-center gap-2 px-3 py-2 rounded-xl bg-linear-to-r from-gray-50 to-gray-100 dark:from-gray-700 dark:to-gray-800 shadow-sm hover:shadow-md transition-all duration-200 cursor-pointer"
    aria-label={$t('user.profile')}
  >
    <div class="w-8 h-8 rounded-full bg-linear-to-br from-blue-500 to-blue-700 flex items-center justify-center text-white font-semibold text-sm shadow-md">
      {auth.auth.user?.username?.charAt(0).toUpperCase() || '?'}
    </div>
    <span class="text-sm font-semibold text-gray-700 dark:text-gray-200 hidden md:inline">
      {auth.auth.user?.username || 'User'}
    </span>
    <ChevronDown class="w-4 h-4 text-gray-500 dark:text-gray-400 hidden md:block transition-transform duration-200 {isOpen ? 'rotate-180' : ''}" />
  </button>

  <!-- Dropdown -->
  {#if isOpen}
    <div class="absolute right-0 mt-2 w-64 bg-white dark:bg-gray-800 rounded-xl shadow-xl border border-gray-200 dark:border-gray-700 py-2 z-[9999]">
      <!-- Profile section -->
      <div class="px-4 py-3 flex items-center gap-3">
        <div class="w-10 h-10 rounded-full bg-linear-to-br from-blue-500 to-blue-700 flex items-center justify-center text-white font-semibold text-base shadow-md">
          {auth.auth.user?.username?.charAt(0).toUpperCase() || '?'}
        </div>
        <div>
          <div class="text-sm font-semibold text-gray-900 dark:text-gray-100">
            {auth.auth.user?.username || 'User'}
          </div>
          {#if auth.auth.user?.role}
            <span class="text-xs px-2 py-0.5 rounded-full bg-blue-100 dark:bg-blue-900 text-blue-700 dark:text-blue-200 font-medium">
              {auth.auth.user.role}
            </span>
          {/if}
        </div>
      </div>

      <div class="border-t border-gray-200 dark:border-gray-700 my-1"></div>

      <!-- Language selector -->
      <div class="px-4 py-2">
        <div class="flex items-center gap-2 mb-2">
          <Globe class="w-4 h-4 text-gray-500 dark:text-gray-400" />
          <span class="text-sm font-medium text-gray-700 dark:text-gray-300">{$t('user.language')}</span>
        </div>
        <div class="flex gap-1">
          {#each availableLocales as language}
            <button
              onclick={() => selectLanguage(language.code)}
              class="flex-1 flex items-center justify-center gap-1.5 px-2 py-1.5 rounded-lg text-xs font-medium transition-colors cursor-pointer
                {language.code === $locale
                  ? 'bg-blue-100 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 ring-1 ring-blue-300 dark:ring-blue-700'
                  : 'text-gray-600 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-700/50'}"
            >
              <span>{language.flag}</span>
              <span>{language.code.toUpperCase()}</span>
            </button>
          {/each}
        </div>
      </div>

      <!-- Theme toggle -->
      <button
        onclick={() => theme.toggle()}
        class="w-full flex items-center justify-between px-4 py-2.5 text-left transition-colors cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-700/50"
      >
        <div class="flex items-center gap-2">
          {#if theme.isDark.current}
            <Moon class="w-4 h-4 text-gray-500 dark:text-gray-400" />
          {:else}
            <Sun class="w-4 h-4 text-gray-500 dark:text-gray-400" />
          {/if}
          <span class="text-sm font-medium text-gray-700 dark:text-gray-300">{$t('user.darkTheme')}</span>
        </div>
        <!-- Toggle switch -->
        <div class="relative w-9 h-5 rounded-full transition-colors {theme.isDark.current ? 'bg-blue-500' : 'bg-gray-300 dark:bg-gray-600'}">
          <div class="absolute top-0.5 left-0.5 w-4 h-4 rounded-full bg-white shadow-sm transition-transform {theme.isDark.current ? 'translate-x-4' : ''}"></div>
        </div>
      </button>

      <div class="border-t border-gray-200 dark:border-gray-700 my-1"></div>

      <!-- Logout -->
      <button
        onclick={handleLogout}
        class="w-full flex items-center gap-2 px-4 py-2.5 text-left text-red-600 dark:text-red-400 hover:bg-red-50 dark:hover:bg-red-900/20 transition-colors cursor-pointer"
      >
        <LogOut class="w-4 h-4" />
        <span class="text-sm font-medium">{$t('auth.logout')}</span>
      </button>
    </div>
  {/if}
</div>
