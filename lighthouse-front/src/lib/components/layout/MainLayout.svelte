<script lang="ts">
  import type { Snippet } from 'svelte';
  import { MediaQuery } from 'svelte/reactivity';
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

  const DESKTOP_SIDEBAR_STORAGE_KEY = 'lighthouse.sidebar.desktopOpen';

  let { children }: Props = $props();

  // Desktop (lg+): sidebar is docked and pushes content, open by default, choice persisted.
  // Mobile/tablet: sidebar is an overlay, closed by default.
  const isDesktop = new MediaQuery('min-width: 1024px');
  let isDesktopSidebarOpen = $state(readDesktopPreference());
  let isMobileSidebarOpen = $state(false);

  const isSidebarOpen = $derived(isDesktop.current ? isDesktopSidebarOpen : isMobileSidebarOpen);
  const isOverlayOpen = $derived(!isDesktop.current && isMobileSidebarOpen);

  function readDesktopPreference(): boolean {
    try {
      return localStorage.getItem(DESKTOP_SIDEBAR_STORAGE_KEY) !== 'false';
    } catch {
      return true;
    }
  }

  function setDesktopSidebarOpen(open: boolean) {
    isDesktopSidebarOpen = open;
    try {
      localStorage.setItem(DESKTOP_SIDEBAR_STORAGE_KEY, String(open));
    } catch {
      // Storage unavailable (private mode, blocked): keep the in-memory value only.
    }
  }

  function toggleSidebar() {
    if (isDesktop.current) {
      setDesktopSidebarOpen(!isDesktopSidebarOpen);
    } else {
      isMobileSidebarOpen = !isMobileSidebarOpen;
    }
  }

  function closeSidebar() {
    if (isDesktop.current) {
      setDesktopSidebarOpen(false);
    } else {
      isMobileSidebarOpen = false;
    }
  }

  function handleSidebarNavigation() {
    isMobileSidebarOpen = false;
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape' && isOverlayOpen) {
      isMobileSidebarOpen = false;
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<div class="flex h-screen bg-gray-100 dark:bg-gray-900 transition-colors">
  <Sidebar
    isOpen={isSidebarOpen}
    docked={isDesktop.current}
    onClose={closeSidebar}
    onNavigate={handleSidebarNavigation}
  />

  {#if isOverlayOpen}
    <button
      type="button"
      class="fixed inset-0 z-[110] bg-black/50 backdrop-blur-[1px]"
      aria-label={$t('common.close')}
      onclick={() => (isMobileSidebarOpen = false)}
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
