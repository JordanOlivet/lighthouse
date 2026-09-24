<script lang="ts">
  import type { Snippet } from 'svelte';
  import Sidebar from './Sidebar.svelte';
  import Header from './Header.svelte';
  import ActionLogPanel from './ActionLogPanel.svelte';
  import ActionLogFab from './ActionLogFab.svelte';
  import ComposeHealthBanner from '$lib/components/compose/ComposeHealthBanner.svelte';
  import { actionLogState } from '$lib/stores/actionLog.svelte';
  import { t } from '$lib/i18n';

  interface Props {
    children: Snippet;
  }

  let { children }: Props = $props();
  let isSidebarOpen = $state(false);

  function toggleSidebar() {
    isSidebarOpen = !isSidebarOpen;
  }

  function closeSidebar() {
    isSidebarOpen = false;
  }

  function handleSidebarNavigation() {
    closeSidebar();
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape' && isSidebarOpen) {
      closeSidebar();
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<div class="flex h-screen bg-gray-100 dark:bg-gray-900 transition-colors">
  <Sidebar isOpen={isSidebarOpen} onClose={closeSidebar} onNavigate={handleSidebarNavigation} />

  {#if isSidebarOpen}
    <button
      type="button"
      class="fixed inset-0 z-[110] bg-black/50 backdrop-blur-[1px]"
      aria-label={$t('common.close')}
      onclick={closeSidebar}
    ></button>
  {/if}

  <div class="flex min-w-0 flex-1 flex-col overflow-hidden">
    <Header onToggleSidebar={toggleSidebar} {isSidebarOpen} />

    <div class="flex min-w-0 flex-1 overflow-hidden">
      <main class="flex-1 overflow-y-auto p-8 lg:p-10 bg-gray-50 dark:bg-gray-900">
        <div class="mx-auto relative">
          <ActionLogFab />
          <ComposeHealthBanner />
          {@render children()}
        </div>
      </main>

      {#if actionLogState.isOpen}
        <ActionLogPanel />
      {/if}
    </div>
  </div>
</div>
