<script lang="ts">
  import { Circle, Zap } from 'lucide-svelte';
  import { t } from '$lib/i18n';
  import type { OneShotStatus } from '$lib/types/compose';
  import { getOneShotDisplay, formatLastRun, type OneShotTone } from '$lib/utils/oneShot';

  interface Props {
    status?: OneShotStatus | null;
    exitCode?: number | null;
    finishedAt?: string | null;
    size?: 'sm' | 'md';
  }

  let { status, exitCode, finishedAt, size = 'sm' }: Props = $props();

  const toneClasses: Record<OneShotTone, string> = {
    success: 'bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-300 border-green-300 dark:border-green-700',
    danger: 'bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-300 border-red-300 dark:border-red-700',
    info: 'bg-blue-100 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 border-blue-300 dark:border-blue-700',
    neutral: 'bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300 border-gray-300 dark:border-gray-600'
  };

  const display = $derived(getOneShotDisplay(status));
  const lastRun = $derived(formatLastRun(finishedAt));
  const label = $derived($t(display.labelKey, { code: exitCode ?? '?' }));
  const sizeClass = $derived(size === 'sm' ? 'text-xs px-2 py-0.5' : 'text-sm px-2.5 py-1');
  const title = $derived(
    [$t('compose.oneShotHint'), lastRun ? $t('compose.oneShotLastRun', { time: lastRun }) : null]
      .filter(Boolean)
      .join('\n')
  );
</script>

<span class="inline-flex flex-col items-start gap-1" {title}>
  <span class="inline-flex items-center gap-1.5">
    <span class="inline-flex items-center gap-1.5 font-medium rounded-full border {toneClasses[display.tone]} {sizeClass}">
      <Circle class="w-2 h-2 fill-current" />
      {label}
    </span>
    <span
      class="inline-flex items-center gap-1 text-[10px] uppercase tracking-wide px-1.5 py-0.5 font-semibold rounded border border-purple-300 dark:border-purple-700 bg-purple-50 dark:bg-purple-900/30 text-purple-700 dark:text-purple-300"
    >
      <Zap class="w-2.5 h-2.5" />
      {$t('compose.oneShotBadge')}
    </span>
  </span>
  {#if lastRun && (status === 'Completed' || status === 'Failed')}
    <span class="text-xs text-gray-500 dark:text-gray-400">
      {$t('compose.oneShotLastRun', { time: lastRun })}
    </span>
  {/if}
</span>
