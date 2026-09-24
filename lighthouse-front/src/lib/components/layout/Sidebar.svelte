<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import {
    LayoutDashboard,
    Package,
    Container,
    HardDrive,
    ScrollText,
    Users,
    UsersRound,
    Shield,
    Settings,
    X
  } from 'lucide-svelte';
  import * as auth from '$lib/stores/auth.svelte';
  import { t } from '$lib/i18n';
  import { systemApi } from '$lib/api';

  interface Props {
    isOpen: boolean;
    onClose?: () => void;
    onNavigate?: () => void;
  }

  let { isOpen, onClose, onNavigate }: Props = $props();
  let version = $state('...');

  onMount(async () => {
    try {
      const versionInfo = await systemApi.getVersion();
      version = versionInfo.version;
    } catch {
      version = 'unknown';
    }
  });

  interface NavItem {
    to: string;
    icon: typeof LayoutDashboard;
    label: string;
    category?: string;
  }

  const adminNavItems: NavItem[] = [
    { to: '/', icon: LayoutDashboard, label: 'navigation.dashboard', category: 'navigation.categories.overview' },
    { to: '/compose/projects', icon: Package, label: 'navigation.composeProjects', category: 'navigation.categories.docker' },
    { to: '/containers', icon: Container, label: 'navigation.containers', category: 'navigation.categories.docker' },
    { to: '/images', icon: HardDrive, label: 'navigation.images', category: 'navigation.categories.docker' },
    { to: '/users', icon: Users, label: 'navigation.userManagement', category: 'navigation.categories.administration' },
    { to: '/user-groups', icon: UsersRound, label: 'navigation.userGroups', category: 'navigation.categories.administration' },
    { to: '/permissions', icon: Shield, label: 'navigation.permissions', category: 'navigation.categories.administration' },
    { to: '/logs-app', icon: ScrollText, label: 'navigation.appLogs', category: 'navigation.categories.administration' },
    { to: '/settings', icon: Settings, label: 'navigation.settings', category: 'navigation.categories.administration' },
  ];

  const userNavItems: NavItem[] = [
    { to: '/', icon: LayoutDashboard, label: 'navigation.dashboard', category: 'navigation.categories.overview' },
    { to: '/compose/projects', icon: Package, label: 'navigation.composeProjects', category: 'navigation.categories.docker' },
    { to: '/containers', icon: Container, label: 'navigation.containers', category: 'navigation.categories.docker' },
  ];

  // Utiliser $derived pour la réactivité quand auth.isAdmin change
  const navItems = $derived(auth.isAdmin.current ? adminNavItems : userNavItems);

  const groupedNavItems = $derived(
    navItems.reduce((acc, item) => {
      const category = item.category ? $t(item.category) : 'Other';
      if (!acc[category]) {
        acc[category] = [];
      }
      acc[category].push(item);
      return acc;
    }, {} as Record<string, NavItem[]>)
  );

  function isActive(path: string, currentPath: string): boolean {
    if (path === '/') {
      return currentPath === '/' || currentPath === '';
    }
    return currentPath.startsWith(path);
  }
</script>

{#if isOpen}
  <aside
    id="app-sidebar"
    class="fixed inset-y-0 left-0 z-[120] flex w-64 flex-col border-r border-gray-200 bg-white shadow-xl transition-colors duration-200 dark:border-gray-800 dark:bg-gray-900"
  >
    <!-- Logo Header -->
    <div class="flex h-16 items-center justify-between border-b border-gray-200 px-6 dark:border-gray-800">
      <div class="flex items-center gap-3">
        <img src="/lighthouse-logo.svg" alt="Lighthouse" class="w-10 h-10 rounded-lg shadow-md" />
        <div>
          <span class="font-bold text-lg tracking-tight text-gray-900 dark:text-white">Lighthouse</span>
          <p class="text-xs text-gray-500 dark:text-gray-400">{$t('app.composeManager')}</p>
        </div>
      </div>
      <button
        type="button"
        class="-mr-2 rounded-lg p-2 text-gray-500 transition-colors hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-white"
        aria-label={$t('common.close')}
        onclick={() => onClose?.()}
      >
        <X class="h-5 w-5" />
      </button>
    </div>

    <!-- Navigation -->
    <nav class="flex-1 overflow-y-auto py-4">
      {#each Object.entries(groupedNavItems) as [category, items]}
        <div class="mb-6">
          <div class="px-6 mb-2">
            <h3 class="text-xs font-semibold text-gray-400 dark:text-gray-500 uppercase tracking-wider">
              {category}
            </h3>
          </div>
          <ul class="space-y-1 px-3">
            {#each items as item}
              {@const active = isActive(item.to, $page.url.pathname)}
              {@const IconComponent = item.icon}
              <li>
                <a
                  href={item.to}
                  onclick={() => onNavigate?.()}
                  class="flex items-center gap-3 px-4 py-2.5 rounded-lg transition-all duration-200 group relative {active
                    ? 'bg-blue-50 dark:bg-blue-900/20 text-blue-600 dark:text-blue-400 shadow-sm'
                    : 'text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800/50 hover:text-gray-900 dark:hover:text-white'}"
                >
                  <!-- Active indicator -->
                  {#if active}
                    <span class="absolute left-0 top-1/2 -translate-y-1/2 w-1 h-8 bg-blue-600 dark:bg-blue-500 rounded-r-full"></span>
                  {/if}
                  <span class="transition-all duration-200 {active ? 'scale-110' : 'group-hover:scale-105'}">
                    <IconComponent class="w-5 h-5" />
                  </span>
                  <span class="font-medium text-sm">{$t(item.label)}</span>
                </a>
              </li>
            {/each}
          </ul>
        </div>
      {/each}
    </nav>

    <!-- Footer -->
    <div class="p-4 border-t border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-900/50">
      <div class="text-xs space-y-1">
        <p class="font-semibold text-gray-700 dark:text-gray-300">{$t('app.title')}</p>
        <p class="text-gray-500 dark:text-gray-500">v{version}</p>
      </div>
    </div>
  </aside>
{/if}
