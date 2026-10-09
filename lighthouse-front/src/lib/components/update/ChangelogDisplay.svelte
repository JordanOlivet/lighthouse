<script lang="ts">
  import { ChevronDown, ChevronRight, AlertTriangle, Shield, ExternalLink, Tag } from 'lucide-svelte';
  import { marked } from 'marked';
  import { sanitizeHtml } from '$lib/utils/sanitizeHtml';
  import { t } from '$lib/i18n';
  import Badge from '$lib/components/ui/badge.svelte';
  import type { ReleaseInfo, ChangelogSummary } from '$lib/types/update';

  interface Props {
    changelog: ReleaseInfo[];
    summary: ChangelogSummary;
    maxVisibleReleases?: number;
    class?: string;
  }

  let { changelog, summary, maxVisibleReleases = 5, class: className = '' }: Props = $props();

  let expandedReleases = $state<Set<string>>(new Set());
  let showAllReleases = $state(false);

  // Configure marked for safe rendering
  marked.setOptions({
    breaks: true, // Convert \n to <br>
    gfm: true, // GitHub Flavored Markdown
  });

  const visibleChangelog = $derived(
    showAllReleases ? changelog : changelog.slice(0, maxVisibleReleases)
  );

  const hasMoreReleases = $derived(changelog.length > maxVisibleReleases);

  function toggleRelease(version: string) {
    if (expandedReleases.has(version)) {
      expandedReleases.delete(version);
    } else {
      expandedReleases.add(version);
    }
    expandedReleases = new Set(expandedReleases);
  }

  function formatDate(dateString: string): string {
    return new Date(dateString).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric'
    });
  }

  function renderMarkdown(text: string): string {
    // Release notes come from GitHub and are injected via {@html}. Always run the
    // output through the sanitizer (including the parse-failure fallback) to strip
    // any scripts / event handlers / unsafe URLs.
    let html: string;
    try {
      html = marked.parse(text) as string;
    } catch {
      html = text;
    }
    return sanitizeHtml(html);
  }
</script>

<div class="space-y-4 {className}">
  {#if summary.totalReleases > 0}
    <!-- Summary badges -->
    <div class="flex flex-wrap gap-2 mb-4">
      <Badge variant="secondary">
        {summary.totalReleases} {summary.totalReleases === 1 ? $t('update.release') : $t('update.releases')}
      </Badge>
      {#if summary.hasSecurityFixes}
        <Badge variant="destructive">
          <Shield class="w-3 h-3 mr-1" />
          {$t('update.securityFixes')}
        </Badge>
      {/if}
      {#if summary.hasBreakingChanges}
        <Badge variant="warning">
          <AlertTriangle class="w-3 h-3 mr-1" />
          {$t('update.breakingChanges')}
        </Badge>
      {/if}
    </div>

    <!-- Release list -->
    <div class="space-y-2">
      {#each visibleChangelog as release (release.version)}
        {@const isExpanded = expandedReleases.has(release.version)}
        <div class="border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden">
          <!-- Release header -->
          <button
            type="button"
            class="w-full flex items-center justify-between gap-2 p-2.5 sm:p-3 bg-gray-50 dark:bg-gray-800/50 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors text-left cursor-pointer"
            onclick={() => toggleRelease(release.version)}
          >
            <div class="flex min-w-0 items-center gap-2 sm:gap-3">
              {#if isExpanded}
                <ChevronDown class="w-4 h-4 shrink-0 text-gray-500" />
              {:else}
                <ChevronRight class="w-4 h-4 shrink-0 text-gray-500" />
              {/if}
              <div class="flex min-w-0 items-center gap-2">
                <Tag class="hidden w-4 h-4 shrink-0 text-gray-500 sm:block" />
                <span class="truncate text-sm font-semibold text-gray-900 dark:text-gray-100 sm:text-base">
                  v{release.version}
                </span>
              </div>
              <div class="flex shrink-0 gap-1">
                {#if release.isSecurityFix}
                  <Badge variant="destructive">
                    <Shield class="w-3 h-3" />
                  </Badge>
                {/if}
                {#if release.isBreakingChange}
                  <Badge variant="warning">
                    <AlertTriangle class="w-3 h-3" />
                  </Badge>
                {/if}
                {#if release.isPreRelease}
                  <Badge variant="secondary">{$t('update.preRelease')}</Badge>
                {/if}
              </div>
            </div>
            <span class="shrink-0 whitespace-nowrap text-xs text-gray-500 dark:text-gray-400 sm:text-sm">
              {formatDate(release.publishedAt)}
            </span>
          </button>

          <!-- Release content (collapsible) -->
          {#if isExpanded}
            <div class="p-3 sm:p-4 border-t border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900">
              {#if release.releaseNotes}
                <div class="prose prose-sm dark:prose-invert max-w-none markdown-content">
                  {@html renderMarkdown(release.releaseNotes)}
                </div>
              {:else}
                <p class="text-sm text-gray-500 dark:text-gray-400 italic">
                  {$t('update.noReleaseNotes')}
                </p>
              {/if}
              {#if release.releaseUrl}
                <div class="mt-3 pt-3 border-t border-gray-200 dark:border-gray-700">
                  <a
                    href={release.releaseUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    class="inline-flex items-center gap-1 text-sm text-primary hover:underline"
                  >
                    {$t('update.viewOnGitHub')}
                    <ExternalLink class="w-3 h-3" />
                  </a>
                </div>
              {/if}
            </div>
          {/if}
        </div>
      {/each}
    </div>

    <!-- Show more/less button -->
    {#if hasMoreReleases}
      <button
        type="button"
        class="w-full py-2 text-sm text-primary hover:underline cursor-pointer"
        onclick={() => showAllReleases = !showAllReleases}
      >
        {#if showAllReleases}
          {$t('update.showLess')}
        {:else}
          {$t('update.showMore', { count: changelog.length - maxVisibleReleases })}
        {/if}
      </button>
    {/if}
  {:else}
    <p class="text-sm text-gray-500 dark:text-gray-400">
      {$t('update.noChangelog')}
    </p>
  {/if}
</div>

<style>
  /* Markdown content styling */
  :global(.markdown-content h1) {
    font-size: 1.5rem;
    font-weight: 700;
    margin-top: 1rem;
    margin-bottom: 0.5rem;
    color: var(--color-gray-900);
  }
  :global(.dark .markdown-content h1) {
    color: var(--color-gray-100);
  }

  :global(.markdown-content h2) {
    font-size: 1.25rem;
    font-weight: 600;
    margin-top: 1rem;
    margin-bottom: 0.5rem;
    color: var(--color-gray-900);
  }
  :global(.dark .markdown-content h2) {
    color: var(--color-gray-100);
  }

  :global(.markdown-content h3) {
    font-size: 1.1rem;
    font-weight: 600;
    margin-top: 0.75rem;
    margin-bottom: 0.25rem;
    color: var(--color-gray-800);
  }
  :global(.dark .markdown-content h3) {
    color: var(--color-gray-200);
  }

  :global(.markdown-content p) {
    margin-bottom: 0.5rem;
    color: var(--color-gray-700);
  }
  :global(.dark .markdown-content p) {
    color: var(--color-gray-300);
  }

  :global(.markdown-content ul),
  :global(.markdown-content ol) {
    margin-left: 1.5rem;
    margin-bottom: 0.5rem;
  }

  :global(.markdown-content ul) {
    list-style-type: disc;
  }

  :global(.markdown-content ol) {
    list-style-type: decimal;
  }

  :global(.markdown-content li) {
    margin-bottom: 0.25rem;
    color: var(--color-gray-700);
  }
  :global(.dark .markdown-content li) {
    color: var(--color-gray-300);
  }

  :global(.markdown-content a) {
    color: var(--color-blue-600);
    text-decoration: underline;
  }
  :global(.dark .markdown-content a) {
    color: var(--color-blue-400);
  }
  :global(.markdown-content a:hover) {
    color: var(--color-blue-800);
  }
  :global(.dark .markdown-content a:hover) {
    color: var(--color-blue-300);
  }

  :global(.markdown-content strong) {
    font-weight: 600;
  }

  :global(.markdown-content code) {
    background-color: var(--color-gray-100);
    padding: 0.125rem 0.25rem;
    border-radius: 0.25rem;
    font-family: monospace;
    font-size: 0.875em;
  }
  :global(.dark .markdown-content code) {
    background-color: var(--color-gray-800);
  }

  :global(.markdown-content pre) {
    background-color: var(--color-gray-100);
    padding: 0.75rem;
    border-radius: 0.375rem;
    overflow-x: auto;
    margin-bottom: 0.5rem;
  }
  :global(.dark .markdown-content pre) {
    background-color: var(--color-gray-800);
  }

  :global(.markdown-content blockquote) {
    border-left: 3px solid var(--color-gray-300);
    padding-left: 1rem;
    margin-left: 0;
    color: var(--color-gray-600);
    font-style: italic;
  }
  :global(.dark .markdown-content blockquote) {
    border-left-color: var(--color-gray-600);
    color: var(--color-gray-400);
  }
</style>
