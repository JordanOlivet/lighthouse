<script lang="ts">
  import { Play, Pause, Trash2, Search, WrapText, Clock } from 'lucide-svelte';
  import { t } from '$lib/i18n';
  import type { AttachedContainer } from '$lib/types';
  import type { LogStreamStatus } from '$lib/stores/logStream.svelte';

  interface Props {
    status: LogStreamStatus;
    paused: boolean;
    count: number;
    showChips: boolean;
    containers: AttachedContainer[];
    selected: Set<string>;
    search: string;
    stderrOnly: boolean;
    showTimestamps: boolean;
    wrap: boolean;
    badgeClassFor: (id: string) => string;
    onTogglePause: () => void;
    onClear: () => void;
    onToggleContainer: (id: string) => void;
  }

  let {
    status,
    paused,
    count,
    showChips,
    containers,
    selected = $bindable(),
    search = $bindable(),
    stderrOnly = $bindable(),
    showTimestamps = $bindable(),
    wrap = $bindable(),
    badgeClassFor,
    onTogglePause,
    onClear,
    onToggleContainer,
  }: Props = $props();

  const statusLabel = $derived(
    status === 'connected'
      ? $t('logs.connected')
      : status === 'connecting' || status === 'reconnecting'
        ? $t('logs.reconnecting')
        : $t('logs.disconnected')
  );

  const statusColor = $derived(
    status === 'connected'
      ? 'bg-green-500'
      : status === 'connecting' || status === 'reconnecting'
        ? 'bg-yellow-500'
        : 'bg-red-500'
  );

  // Touch-sized (36px) on mobile, compact from sm up.
  const btnBase = 'flex h-9 min-w-9 items-center justify-center gap-1 rounded-md border sm:h-auto sm:min-w-0';
</script>

<div class="flex flex-col gap-2 border-b border-gray-200 dark:border-gray-700 p-2">
  <div class="flex flex-wrap items-center gap-2">
    <!-- Search takes its own full-width row on mobile. -->
    <div class="relative order-first basis-full sm:order-none sm:basis-auto sm:flex-1 sm:min-w-[140px]">
      <Search class="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-gray-400" />
      <input
        type="search"
        bind:value={search}
        placeholder={$t('logs.searchPlaceholder')}
        class="w-full h-9 pl-8 pr-2 text-sm rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-900 text-gray-900 dark:text-gray-100 sm:h-auto sm:py-1 sm:pl-7 sm:text-xs"
      />
    </div>

    <span
      class="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400 sm:order-first"
      title={statusLabel}
    >
      <span class="inline-block w-2 h-2 rounded-full {statusColor}"></span>
      <span class="hidden sm:inline">{statusLabel}</span>
      <span class="sr-only sm:hidden">{statusLabel}</span>
    </span>

    <button
      type="button"
      onclick={() => (stderrOnly = !stderrOnly)}
      class="{btnBase} px-2 text-xs sm:py-1 {stderrOnly
        ? 'border-red-400 text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-950'
        : 'border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300'}"
      title={$t('logs.stderrOnly')}
      aria-pressed={stderrOnly}
    >
      stderr
    </button>

    <button
      type="button"
      onclick={() => (showTimestamps = !showTimestamps)}
      class="{btnBase} sm:p-1 {showTimestamps
        ? 'border-blue-400 text-blue-600 dark:text-blue-400'
        : 'border-gray-300 dark:border-gray-600 text-gray-500'}"
      title={$t('logs.timestamps')}
      aria-label={$t('logs.timestamps')}
      aria-pressed={showTimestamps}
    >
      <Clock class="w-4 h-4" />
    </button>

    <button
      type="button"
      onclick={() => (wrap = !wrap)}
      class="{btnBase} sm:p-1 {wrap
        ? 'border-blue-400 text-blue-600 dark:text-blue-400'
        : 'border-gray-300 dark:border-gray-600 text-gray-500'}"
      title={$t('logs.wrap')}
      aria-label={$t('logs.wrap')}
      aria-pressed={wrap}
    >
      <WrapText class="w-4 h-4" />
    </button>

    <button
      type="button"
      onclick={onTogglePause}
      class="{btnBase} px-2 text-xs border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 sm:py-1"
      title={paused ? $t('logs.resume') : $t('logs.pause')}
      aria-label={paused ? $t('logs.resume') : $t('logs.pause')}
    >
      {#if paused}
        <Play class="w-4 h-4 sm:w-3.5 sm:h-3.5" /> <span class="hidden sm:inline">{$t('logs.resume')}</span>
      {:else}
        <Pause class="w-4 h-4 sm:w-3.5 sm:h-3.5" /> <span class="hidden sm:inline">{$t('logs.pause')}</span>
      {/if}
    </button>

    <button
      type="button"
      onclick={onClear}
      class="{btnBase} px-2 border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 sm:py-1"
      title={$t('logs.clear')}
      aria-label={$t('logs.clear')}
    >
      <Trash2 class="w-4 h-4 sm:w-3.5 sm:h-3.5" />
    </button>

    <span class="ml-auto text-xs text-gray-400 tabular-nums sm:ml-0">{count}</span>
  </div>

  {#if showChips && containers.length > 0}
    <!-- Single scrollable row on mobile so chips don't push the log pane down. -->
    <div class="-mx-2 flex gap-1.5 overflow-x-auto px-2 sm:mx-0 sm:flex-wrap sm:overflow-visible sm:px-0">
      {#each containers as container (container.id)}
        {@const active = selected.size === 0 || selected.has(container.id)}
        <button
          type="button"
          onclick={() => onToggleContainer(container.id)}
          class="flex shrink-0 items-center gap-1.5 px-2.5 py-1 rounded-full text-xs border bg-white/70 dark:bg-gray-800/70 text-gray-700 dark:text-gray-200 transition-opacity sm:px-2 sm:py-0.5 sm:text-[11px] {active
            ? 'border-gray-300 dark:border-gray-600'
            : 'border-gray-200 dark:border-gray-700 opacity-40'}"
          aria-pressed={active}
        >
          <span class="inline-block w-2 h-2 rounded-full {badgeClassFor(container.id)}"></span>
          {container.service ?? container.name}
        </button>
      {/each}
    </div>
  {/if}
</div>

<style>
  button {
    cursor: pointer;
  }
</style>
