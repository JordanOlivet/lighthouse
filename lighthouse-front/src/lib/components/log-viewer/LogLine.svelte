<script lang="ts">
  import type { LogEntry } from '$lib/types';
  import { parseAnsi, type AnsiSegment } from '$lib/utils/ansi';

  interface Props {
    entry: LogEntry;
    showTimestamp: boolean;
    showBadge: boolean;
    badgeClass: string;
    wrap: boolean;
    search: string;
  }

  let { entry, showTimestamp, showBadge, badgeClass, wrap, search }: Props = $props();

  const segments = $derived<AnsiSegment[]>(parseAnsi(entry.message));

  // Splits into HH:MM:SS and the ".mmm" suffix so the milliseconds can be hidden on mobile.
  function formatTimestamp(ts: string): { time: string; ms: string } {
    if (!ts) return { time: '', ms: '' };
    const tIndex = ts.indexOf('T');
    if (tIndex < 0) return { time: ts, ms: '' };
    const time = ts.slice(tIndex + 1).replace('Z', '');
    const dot = time.indexOf('.');
    if (dot < 0) return { time, ms: '' };
    return { time: time.slice(0, dot), ms: time.slice(dot, dot + 4) };
  }

  const timestamp = $derived(formatTimestamp(entry.timestamp));

  // Splits a segment's text into plain / highlighted parts for the search match.
  function highlightParts(text: string, term: string): { text: string; hit: boolean }[] {
    if (!term) return [{ text, hit: false }];
    const lower = text.toLowerCase();
    const needle = term.toLowerCase();
    const parts: { text: string; hit: boolean }[] = [];
    let from = 0;
    let idx = lower.indexOf(needle, from);
    while (idx >= 0) {
      if (idx > from) parts.push({ text: text.slice(from, idx), hit: false });
      parts.push({ text: text.slice(idx, idx + needle.length), hit: true });
      from = idx + needle.length;
      idx = lower.indexOf(needle, from);
    }
    if (from < text.length) parts.push({ text: text.slice(from), hit: false });
    return parts;
  }

  function styleFor(seg: AnsiSegment): string {
    const rules: string[] = [];
    if (seg.fg) rules.push(`color:${seg.fg}`);
    if (seg.bg) rules.push(`background-color:${seg.bg}`);
    if (seg.bold) rules.push('font-weight:600');
    if (seg.dim) rules.push('opacity:0.7');
    if (seg.italic) rules.push('font-style:italic');
    if (seg.underline) rules.push('text-decoration:underline');
    return rules.join(';');
  }
</script>

<div
  class="log-line flex gap-2 px-2 py-0.5 {entry.stream === 'stderr' ? 'stderr' : ''} {wrap
    ? 'max-md:border-b max-md:border-gray-200/70 max-md:py-1 dark:max-md:border-gray-800'
    : ''}"
  class:whitespace-pre-wrap={wrap}
  class:break-all={wrap}
  class:whitespace-pre={!wrap}
>
  {#if showTimestamp}
    <span class="shrink-0 select-none text-gray-400 dark:text-gray-500 tabular-nums">{timestamp.time}<span class="hidden sm:inline">{timestamp.ms}</span></span>
  {/if}
  {#if showBadge}
    <span
      class="shrink-0 self-start px-1.5 rounded text-white text-[10px] font-semibold leading-5 max-w-[140px] truncate {badgeClass}"
      title={entry.service ?? entry.containerName}
    >
      {entry.service ?? entry.containerName}
    </span>
  {/if}
  <span class="min-w-0 flex-1">
    {#each segments as seg}<span style={styleFor(seg)}>{#each highlightParts(seg.text, search) as part}{#if part.hit}<mark class="bg-yellow-300 text-black rounded-sm">{part.text}</mark>{:else}{part.text}{/if}{/each}</span>{/each}
  </span>
</div>

<style>
  .log-line.stderr {
    border-left: 2px solid rgb(239 68 68);
    background-color: rgb(239 68 68 / 0.06);
  }
</style>
