<script lang="ts">
  import { Menu, Download } from 'lucide-svelte';
  import { goto } from '$app/navigation';
  import { isAdmin } from '$lib/stores/auth.svelte';
  import { hasUpdate, updateCount } from '$lib/stores/update.svelte';
  import UserMenu from './UserMenu.svelte';
  import { t } from '$lib/i18n';

  interface Props {
    onToggleSidebar: () => void;
    isSidebarOpen: boolean;
  }

  let { onToggleSidebar, isSidebarOpen }: Props = $props();

  function goToSettings() {
    goto('/settings');
  }
</script>

<header class="bg-white dark:bg-gray-800 shadow-md border-b border-gray-200 dark:border-gray-700 transition-all backdrop-blur-sm bg-opacity-95 relative z-[100]">
  <div class="flex items-center justify-between h-16 px-6">
    <div class="flex items-center gap-4">
      <button
        onclick={onToggleSidebar}
        class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 transition-all duration-200 hover:scale-105 cursor-pointer"
        aria-label={$t('common.toggleSidebar')}
        aria-controls="app-sidebar"
        aria-expanded={isSidebarOpen}
      >
        <Menu class="w-5 h-5 text-gray-600 dark:text-gray-300" />
      </button>

      <div class="flex items-center gap-2">
        <img src="/lighthouse-logo.svg" alt="Lighthouse" class="w-8 h-8 rounded-md hidden sm:block" />
        <h1 class="text-xl font-bold text-transparent bg-clip-text bg-linear-to-r from-blue-600 to-blue-800 dark:from-blue-400 dark:to-blue-600 hidden sm:block">
          {$t('app.title')}
        </h1>
      </div>
    </div>

    <div class="flex items-center gap-3">
      <!-- Update Available Badge (Admin only) -->
      {#if isAdmin.current && hasUpdate.current}
        <button
          onclick={goToSettings}
          class="relative p-2 rounded-lg hover:bg-green-50 dark:hover:bg-green-900/20 transition-all duration-200 group cursor-pointer"
          title={$t('update.updateAvailable')}
        >
          <Download class="w-5 h-5 text-green-600 dark:text-green-400 group-hover:scale-110 transition-transform" />
          {#if updateCount.current > 0}
            <span class="absolute -top-1 -right-1 w-5 h-5 flex items-center justify-center bg-green-500 text-white text-xs font-bold rounded-full shadow-sm">
              {updateCount.current > 9 ? '9+' : updateCount.current}
            </span>
          {/if}
        </button>
      {/if}

      <UserMenu />
    </div>
  </div>
</header>
