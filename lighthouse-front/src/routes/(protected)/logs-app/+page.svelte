<script lang="ts">
  import { onMount, onDestroy, tick } from 'svelte';
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { Play, Pause, Trash2, Download, RefreshCw, ScrollText, SlidersHorizontal, ArrowDownToLine, ChevronDown } from 'lucide-svelte';
  import { AppLogStreamController } from '$lib/stores/appLogStream.svelte';
  import type { AppLogEntry, AppLogFilter } from '$lib/api/appLogs';
  import { configApi } from '$lib/api';
  import { Button, Input, Card, CardHeader, CardTitle, CardContent } from '$lib/components';
  import { t } from '$lib/i18n';
  import { toast } from 'svelte-sonner';

  // Serilog levels, most to least severe. The severity ordering also drives the
  // colour ramp below.
  const ALL_LEVELS = ['Fatal', 'Error', 'Warning', 'Information', 'Debug', 'Verbose'];

  const levelClasses: Record<string, string> = {
    Fatal: 'text-fuchsia-400',
    Error: 'text-red-400',
    Warning: 'text-amber-400',
    Information: 'text-sky-400',
    Debug: 'text-gray-400',
    Verbose: 'text-gray-500'
  };

  // Filter state (applied on change via $effect below).
  let selectedLevels = $state<Set<string>>(new Set());
  let category = $state('');
  let user = $state('');
  let search = $state('');

  let autoScroll = $state(true);
  let logsContainer = $state<HTMLDivElement>();

  // Filters start collapsed on mobile so the log pane is visible without scrolling.
  let filtersOpen = $state(window.matchMedia('(min-width: 768px)').matches);
  const activeFilterCount = $derived(
    selectedLevels.size + (category.trim() ? 1 : 0) + (user.trim() ? 1 : 0)
  );

  // Serilog-style 3-letter level codes, used on narrow screens.
  const shortLevels: Record<string, string> = {
    Fatal: 'FTL',
    Error: 'ERR',
    Warning: 'WRN',
    Information: 'INF',
    Debug: 'DBG',
    Verbose: 'VRB'
  };

  const controller = new AppLogStreamController();
  const entries = $derived(controller.entries);
  const status = $derived(controller.status);

  const queryClient = useQueryClient();

  // Runtime log level, reusing the existing Settings endpoint.
  const logLevelQuery = createQuery(() => ({
    queryKey: ['log-level'],
    queryFn: () => configApi.getLogLevel()
  }));

  const updateLogLevelMutation = createMutation(() => ({
    mutationFn: (value: string) => configApi.updateLogLevel(value),
    onSuccess: (data) => {
      queryClient.setQueryData(['log-level'], data);
      toast.success($t('appLogs.logLevelSaved'));
    },
    onError: () => {
      toast.error($t('errors.generic'));
      queryClient.invalidateQueries({ queryKey: ['log-level'] });
    }
  }));

  function handleLogLevelChange(e: Event) {
    updateLogLevelMutation.mutate((e.target as HTMLSelectElement).value);
  }

  function currentFilter(): AppLogFilter {
    return {
      levels: selectedLevels.size > 0 ? [...selectedLevels] : undefined,
      category: category.trim() || undefined,
      user: user.trim() || undefined,
      search: search.trim() || undefined
    };
  }

  // Debounce filter changes into a single stream restart.
  let restartTimer: ReturnType<typeof setTimeout> | undefined;
  $effect(() => {
    // Track the reactive inputs so this effect re-runs when they change.
    void selectedLevels;
    void category;
    void user;
    void search;

    clearTimeout(restartTimer);
    const filter = currentFilter();
    restartTimer = setTimeout(() => controller.start(filter), 300);
  });

  // Auto-scroll to the bottom as new lines arrive, unless the user opted out.
  $effect(() => {
    void entries.length;
    if (autoScroll && logsContainer) {
      tick().then(() => {
        if (logsContainer) logsContainer.scrollTop = logsContainer.scrollHeight;
      });
    }
  });

  onMount(() => {
    controller.start(currentFilter());
  });

  onDestroy(() => {
    clearTimeout(restartTimer);
    controller.destroy();
  });

  function toggleLevel(level: string) {
    const next = new Set(selectedLevels);
    if (next.has(level)) next.delete(level);
    else next.add(level);
    selectedLevels = next;
  }

  function togglePause() {
    if (controller.paused) controller.resume();
    else controller.pause();
  }

  function clearLogs() {
    controller.clear();
  }

  function formatTimestamp(ts: string): string {
    const d = new Date(ts);
    return Number.isNaN(d.getTime()) ? ts : d.toISOString().replace('T', ' ').replace('Z', '');
  }

  // Splits the formatted timestamp so the date and milliseconds can be hidden on mobile.
  function timestampParts(ts: string): { date: string; time: string; ms: string } {
    const full = formatTimestamp(ts);
    const match = /^(\S+ )(\d{2}:\d{2}:\d{2})(\.\d+)?$/.exec(full);
    if (!match) return { date: '', time: full, ms: '' };
    return { date: match[1], time: match[2], ms: match[3] ?? '' };
  }

  function shortCategory(category?: string): string {
    if (!category) return '';
    // Show the last segment of the namespace to keep lines readable.
    const parts = category.split('.');
    return parts[parts.length - 1];
  }

  interface TextSegment {
    text: string;
    match: boolean;
  }

  function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  // Split text into alternating non-match / match segments for the active search term
  // (case-insensitive). Returns a single non-match segment when there is no term.
  function splitOnMatch(text: string, term: string): TextSegment[] {
    const trimmed = term.trim();
    if (!trimmed) return [{ text, match: false }];

    const regex = new RegExp(`(${escapeRegExp(trimmed)})`, 'ig');
    return text
      .split(regex)
      .filter((part) => part !== '')
      .map((part) => ({ text: part, match: part.toLowerCase() === trimmed.toLowerCase() }));
  }

  function downloadLogs() {
    const text = entries
      .map((e: AppLogEntry) => {
        const base = `${formatTimestamp(e.timestamp)} [${e.level}] ${e.category ?? ''} ${e.username ? `(${e.username}) ` : ''}${e.message}`;
        return e.exception ? `${base}\n${e.exception}` : base;
      })
      .join('\n');
    const blob = new Blob([text], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `app-logs-${new Date().toISOString().replace(/:/g, '-')}.log`;
    a.click();
    URL.revokeObjectURL(url);
  }
</script>

<div class="space-y-4 sm:space-y-6">
  <!-- Header -->
  <div class="flex items-start justify-between gap-3 flex-wrap sm:gap-4">
    <div class="min-w-0">
      <h1 class="text-2xl font-bold text-gray-900 dark:text-white flex items-center gap-2 sm:text-3xl sm:gap-3">
        <ScrollText class="w-6 h-6 shrink-0 text-blue-600 dark:text-blue-400 sm:w-8 sm:h-8" />
        {$t('appLogs.title')}
      </h1>
      <p class="text-sm text-gray-600 dark:text-gray-400 mt-1 sm:text-base">{$t('appLogs.subtitle')}</p>
    </div>
  </div>

  <!-- Filters: search always visible; the rest collapses on mobile -->
  <Card>
    <CardHeader class="p-4 sm:p-6">
      <button
        type="button"
        onclick={() => (filtersOpen = !filtersOpen)}
        aria-expanded={filtersOpen}
        aria-controls="app-logs-filters"
        class="flex min-h-9 w-full items-center gap-2 text-left md:cursor-default"
      >
        <SlidersHorizontal class="h-4 w-4 text-gray-500 md:hidden" />
        <CardTitle class="text-base sm:text-2xl">{$t('appLogs.filters')}</CardTitle>
        {#if activeFilterCount > 0}
          <span class="rounded-full bg-blue-600 px-2 py-0.5 text-xs font-semibold text-white md:hidden">{activeFilterCount}</span>
        {/if}
        <ChevronDown class="ml-auto h-4 w-4 text-gray-500 transition-transform md:hidden {filtersOpen ? 'rotate-180' : ''}" />
      </button>
    </CardHeader>
    <CardContent class="space-y-3 p-4 pt-0 sm:space-y-4 sm:p-6 sm:pt-0">
      <Input type="search" bind:value={search} placeholder={$t('appLogs.searchPlaceholder')} class="md:hidden" />

      <div id="app-logs-filters" class="space-y-3 sm:space-y-4 {filtersOpen ? '' : 'max-md:hidden'}">
      <!-- Level chips -->
      <div class="flex flex-wrap gap-2">
        {#each ALL_LEVELS as level (level)}
          {@const active = selectedLevels.has(level)}
          <button
            type="button"
            onclick={() => toggleLevel(level)}
            aria-pressed={active}
            class="min-h-9 px-3 py-1 rounded-full text-xs font-semibold border transition-colors sm:min-h-0 {active
              ? 'bg-blue-600 border-blue-600 text-white'
              : 'border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800'}"
          >
            {level}
          </button>
        {/each}
        {#if selectedLevels.size > 0}
          <button
            type="button"
            onclick={() => (selectedLevels = new Set())}
            class="min-h-9 px-3 py-1 rounded-full text-xs font-medium text-gray-500 hover:text-gray-700 dark:hover:text-gray-300 sm:min-h-0"
          >
            {$t('appLogs.clearLevels')}
          </button>
        {/if}
      </div>

      <div class="grid grid-cols-1 md:grid-cols-4 gap-3 sm:gap-4">
        <Input type="search" bind:value={search} placeholder={$t('appLogs.searchPlaceholder')} class="max-md:hidden" />
        <Input type="text" bind:value={category} placeholder={$t('appLogs.categoryPlaceholder')} />
        <Input type="text" bind:value={user} placeholder={$t('appLogs.userPlaceholder')} />
        <!-- Runtime log level (reuses Settings endpoint) -->
        <div class="relative">
          <select
            value={logLevelQuery.data?.current}
            onchange={handleLogLevelChange}
            disabled={logLevelQuery.isLoading || updateLogLevelMutation.isPending}
            aria-label={$t('appLogs.logLevel')}
            title={$t('appLogs.logLevel')}
            class="h-10 w-full px-3 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-800 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50 cursor-pointer text-sm"
          >
            {#each logLevelQuery.data?.available ?? [] as level (level)}
              <option value={level}>{level}</option>
            {/each}
          </select>
          {#if updateLogLevelMutation.isPending}
            <RefreshCw class="pointer-events-none absolute right-8 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-gray-500" />
          {/if}
        </div>
      </div>
      </div>
    </CardContent>
  </Card>

  <!-- Logs -->
  <Card>
    <CardHeader class="p-4 sm:p-6">
      <div class="flex items-center justify-between gap-2 flex-wrap sm:gap-4">
        <CardTitle class="text-base sm:text-2xl">
          {$t('appLogs.linesCount', { count: entries.length })}
        </CardTitle>
        <div class="flex items-center gap-1.5 flex-wrap sm:gap-2">
          <div class="flex items-center gap-2 mr-1 sm:mr-2" title={$t(`appLogs.status.${status}`)}>
            <span
              class="w-2 h-2 rounded-full {status === 'connected'
                ? 'bg-green-500 animate-pulse'
                : status === 'reconnecting' || status === 'connecting'
                  ? 'bg-amber-500 animate-pulse'
                  : 'bg-gray-400'}"
            ></span>
            <span class="sr-only text-sm text-gray-600 dark:text-gray-400 sm:not-sr-only">{$t(`appLogs.status.${status}`)}</span>
          </div>
          <!-- Icon-only on mobile; labels come back from sm up. -->
          <Button
            variant="outline"
            size="sm"
            class="w-9 px-0 sm:w-auto sm:px-3"
            onclick={togglePause}
            title={controller.paused ? $t('appLogs.resume') : $t('appLogs.pause')}
            aria-label={controller.paused ? $t('appLogs.resume') : $t('appLogs.pause')}
          >
            {#if controller.paused}
              <Play class="w-4 h-4 sm:mr-2" /><span class="hidden sm:inline">{$t('appLogs.resume')}</span>
            {:else}
              <Pause class="w-4 h-4 sm:mr-2" /><span class="hidden sm:inline">{$t('appLogs.pause')}</span>
            {/if}
          </Button>
          <Button
            variant="outline"
            size="sm"
            class="w-9 px-0 sm:w-auto sm:px-3"
            onclick={clearLogs}
            disabled={entries.length === 0}
            title={$t('appLogs.clear')}
            aria-label={$t('appLogs.clear')}
          >
            <Trash2 class="w-4 h-4 sm:mr-2" /><span class="hidden sm:inline">{$t('appLogs.clear')}</span>
          </Button>
          <Button
            variant="outline"
            size="sm"
            class="w-9 px-0 sm:w-auto sm:px-3"
            onclick={downloadLogs}
            disabled={entries.length === 0}
            title={$t('appLogs.download')}
            aria-label={$t('appLogs.download')}
          >
            <Download class="w-4 h-4 sm:mr-2" /><span class="hidden sm:inline">{$t('appLogs.download')}</span>
          </Button>
          <!-- Auto-scroll: toggle button on mobile, checkbox from sm up. -->
          <button
            type="button"
            onclick={() => (autoScroll = !autoScroll)}
            aria-pressed={autoScroll}
            title={$t('appLogs.autoScroll')}
            aria-label={$t('appLogs.autoScroll')}
            class="flex h-9 w-9 items-center justify-center rounded-md border sm:hidden {autoScroll
              ? 'border-blue-400 text-blue-600 dark:text-blue-400'
              : 'border-gray-300 dark:border-gray-600 text-gray-500'}"
          >
            <ArrowDownToLine class="h-4 w-4" />
          </button>
          <label class="hidden items-center gap-2 cursor-pointer text-sm text-gray-700 dark:text-gray-300 sm:flex">
            <input type="checkbox" bind:checked={autoScroll} class="h-4 w-4" />
            {$t('appLogs.autoScroll')}
          </label>
        </div>
      </div>
    </CardHeader>
    <CardContent class="p-2 pt-0 sm:p-6 sm:pt-0">
      <div
        bind:this={logsContainer}
        class="bg-gray-900 dark:bg-black rounded-lg p-1.5 font-mono text-xs text-gray-100 h-[70dvh] overflow-auto sm:p-4 md:h-auto md:max-h-[600px]"
      >
        {#if entries.length === 0}
          <p class="p-2 text-gray-500 sm:p-0">{$t('appLogs.noLogs')}</p>
        {:else}
          {#each entries as entry, i (i)}
            {@const ts = timestampParts(entry.timestamp)}
            <div class="py-0.5 hover:bg-gray-800/50 rounded px-2 whitespace-pre-wrap break-all max-md:rounded-none max-md:border-b max-md:border-gray-800 max-md:py-1">
              <span class="text-gray-500"><span class="hidden md:inline">{ts.date}</span>{ts.time}<span class="hidden md:inline">{ts.ms}</span></span>
              <span class="font-semibold {levelClasses[entry.level] ?? 'text-gray-300'}" title={entry.level}> <span class="md:hidden">{shortLevels[entry.level] ?? entry.level}</span><span class="hidden md:inline">[{entry.level}]</span></span>
              {#if entry.category}
                <span class="text-purple-400" title={entry.category}> {shortCategory(entry.category)}</span>
              {/if}
              {#if entry.username}
                <span class="text-teal-400"> ({entry.username})</span>
              {/if}
              <span class="text-gray-100"> {#each splitOnMatch(entry.message, search) as seg, si (si)}{#if seg.match}<mark class="bg-yellow-400/30 text-yellow-200 rounded-sm">{seg.text}</mark>{:else}{seg.text}{/if}{/each}</span>
              {#if entry.exception}
                <div class="text-red-300 mt-1 pl-4">{#each splitOnMatch(entry.exception, search) as seg, si (si)}{#if seg.match}<mark class="bg-yellow-400/30 text-yellow-200 rounded-sm">{seg.text}</mark>{:else}{seg.text}{/if}{/each}</div>
              {/if}
            </div>
          {/each}
        {/if}
      </div>
    </CardContent>
  </Card>
</div>
