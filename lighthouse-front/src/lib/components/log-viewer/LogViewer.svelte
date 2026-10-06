<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { FileText, AlertCircle, Loader2, ArrowDown } from 'lucide-svelte';
  import { t } from '$lib/i18n';
  import type { LogEntry } from '$lib/types';
  import { LogStreamController, type LogStreamMode } from '$lib/stores/logStream.svelte';
  import LogLine from './LogLine.svelte';
  import LogToolbar from './LogToolbar.svelte';

  interface Props {
    mode: 'container' | 'project';
    containerId?: string;
    containerName?: string;
    projectName?: string;
  }

  let { mode, containerId, containerName, projectName }: Props = $props();

  const isProject = mode === 'project';

  // Deterministic badge palette keyed by container id (stable across renders).
  const palette = [
    'bg-blue-600', 'bg-green-600', 'bg-purple-600', 'bg-orange-600', 'bg-pink-600',
    'bg-cyan-600', 'bg-indigo-600', 'bg-teal-600', 'bg-red-600', 'bg-yellow-600',
  ];
  function badgeClassFor(id: string): string {
    let hash = 0;
    for (let i = 0; i < id.length; i++) hash = (hash * 31 + id.charCodeAt(i)) >>> 0;
    return palette[hash % palette.length];
  }

  let controller = $state<LogStreamController>();

  // View state
  let search = $state('');
  let stderrOnly = $state(false);
  let showTimestamps = $state(true);
  // Wrap by default on narrow screens: horizontal scrolling a log pane on touch is unusable.
  let wrap = $state(window.matchMedia('(max-width: 767px)').matches);
  let selected = $state(new Set<string>());
  let autoFollow = $state(true);

  let scrollContainer = $state<HTMLDivElement>();
  let content = $state<HTMLDivElement>();

  onMount(() => {
    const streamMode: LogStreamMode = isProject
      ? { type: 'project', name: projectName ?? '' }
      : { type: 'container', id: containerId ?? '', name: containerName ?? '' };
    const c = new LogStreamController(streamMode);
    controller = c;
    c.start();
    return () => c.destroy();
  });

  const entries = $derived(controller?.entries ?? []);

  const filtered = $derived.by<LogEntry[]>(() => {
    const term = search.toLowerCase();
    return entries.filter((e) => {
      if (selected.size > 0 && !selected.has(e.containerId)) return false;
      if (stderrOnly && e.stream !== 'stderr') return false;
      if (term && !e.message.toLowerCase().includes(term)) return false;
      return true;
    });
  });

  // Auto-follow: stick to the bottom as new lines arrive, unless the user scrolled up.
  // A ResizeObserver on the content re-pins after the list's real height settles —
  // content-visibility only measures off-screen rows lazily, so a one-shot scroll on
  // length change lands short. Re-pinning on every resize converges to the bottom.
  $effect(() => {
    const list = content;
    const sc = scrollContainer;
    if (!list || !sc) return;

    const observer = new ResizeObserver(() => {
      if (autoFollow) sc.scrollTop = sc.scrollHeight;
    });
    observer.observe(list);
    return () => observer.disconnect();
  });

  async function handleScroll() {
    const el = scrollContainer;
    if (!el || !controller) return;

    const distanceToBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    autoFollow = distanceToBottom < 50;

    // Near the top → load an older page, anchoring the scroll position.
    if (el.scrollTop < 60 && controller.hasMore && !controller.loadingOlder) {
      const prevHeight = el.scrollHeight;
      const added = await controller.loadOlder();
      if (added > 0) {
        await tick();
        el.scrollTop = el.scrollHeight - prevHeight + el.scrollTop;
      }
    }
  }

  function scrollToBottom() {
    autoFollow = true;
    if (scrollContainer) scrollContainer.scrollTop = scrollContainer.scrollHeight;
  }

  function toggleContainer(id: string) {
    const next = new Set(selected);
    // Empty set means "all"; first click narrows to just this one.
    if (next.size === 0) {
      for (const c of controller?.containers ?? []) {
        if (c.id !== id) next.add(c.id);
      }
    } else if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    selected = next;
  }
</script>

<div class="flex flex-col h-full bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg overflow-hidden">
  <div class="flex items-center gap-2 px-3 py-2 bg-white dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700">
    <FileText class="w-4 h-4 text-gray-500" />
    <h3 class="text-sm font-semibold text-gray-900 dark:text-white truncate">
      {isProject ? $t('logs.projectTitle') : $t('logs.containerTitle')}
      {#if isProject ? projectName : containerName}
        <span class="text-gray-400 font-normal">— {isProject ? projectName : containerName}</span>
      {/if}
    </h3>
  </div>

  {#if controller}
    <LogToolbar
      status={controller.status}
      paused={controller.paused}
      count={filtered.length}
      showChips={isProject}
      containers={controller.containers}
      bind:selected
      bind:search
      bind:stderrOnly
      bind:showTimestamps
      bind:wrap
      {badgeClassFor}
      onTogglePause={() => (controller!.paused ? controller!.resume() : controller!.pause())}
      onClear={() => controller!.clear()}
      onToggleContainer={toggleContainer}
    />
  {/if}

  {#if controller?.error}
    <div class="flex items-center gap-2 px-3 py-1.5 text-xs text-red-600 dark:text-red-400 bg-red-50 dark:bg-red-950">
      <AlertCircle class="w-3.5 h-3.5 shrink-0" />
      {controller.error}
    </div>
  {/if}

  <div class="relative flex-1 min-h-0">
    <div
      bind:this={scrollContainer}
      onscroll={handleScroll}
      class="absolute inset-0 overflow-y-auto font-mono text-xs bg-gray-50 dark:bg-gray-900 text-gray-800 dark:text-gray-200"
    >
      {#if controller?.loadingOlder}
        <div class="flex justify-center py-1.5 text-gray-400">
          <Loader2 class="w-4 h-4 animate-spin" />
        </div>
      {/if}

      {#if filtered.length === 0}
        <div class="flex items-center justify-center h-full text-gray-400 text-sm">
          {controller?.paused ? $t('logs.paused') : $t('logs.waiting')}
        </div>
      {:else}
        <div bind:this={content}>
          {#each filtered as entry (entry)}
            <div class="cv">
              <LogLine
                {entry}
                showTimestamp={showTimestamps}
                showBadge={isProject}
                badgeClass={badgeClassFor(entry.containerId)}
                {wrap}
                {search}
              />
            </div>
          {/each}
        </div>
      {/if}
    </div>

    {#if !autoFollow && filtered.length > 0}
      <button
        type="button"
        onclick={scrollToBottom}
        class="absolute bottom-3 right-3 flex min-h-9 items-center gap-1 px-3 py-1.5 text-xs rounded-full sm:min-h-0 sm:px-2.5 shadow-lg cursor-pointer bg-blue-600 hover:bg-blue-500 text-white transition-colors"
        title={$t('logs.goToBottom')}
      >
        <ArrowDown class="w-3.5 h-3.5" />
        {$t('logs.goToBottom')}
      </button>
    {/if}
  </div>
</div>

<style>
  .cv {
    content-visibility: auto;
    contain-intrinsic-size: auto 1.25rem;
  }
</style>
