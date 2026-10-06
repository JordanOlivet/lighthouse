<script lang="ts">
  import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
  import { HardDrive, Trash2, Search, Loader2, Check } from 'lucide-svelte';
  import { formatDistanceToNow } from 'date-fns';
  import { enUS, fr, es } from 'date-fns/locale';
  import { imagesApi } from '$lib/api';
  import LoadingState from '$lib/components/common/LoadingState.svelte';
  import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
  import ActionButton from '$lib/components/common/ActionButton.svelte';
  import DraggableTableHeader from '$lib/components/common/DraggableTableHeader.svelte';
  import BulkImageDeleteDialog from '$lib/components/images/BulkImageDeleteDialog.svelte';
  import Badge from '$lib/components/ui/badge.svelte';
  import Button from '$lib/components/ui/button.svelte';
  import Input from '$lib/components/ui/input.svelte';
  import Checkbox from '$lib/components/ui/checkbox.svelte';
  import { t, locale as localeStore } from '$lib/i18n';
  import { toast } from 'svelte-sonner';
  import { formatBytes } from '$lib/utils/units';
  import { createColumnPreferences } from '$lib/stores/columnPreferences.svelte';
  import type { ColumnDefinition } from '$lib/types/table';
  import type { Image, PruneImagesResult } from '$lib/types';

  const imageColumns: ColumnDefinition[] = [
    { id: 'select', labelKey: 'images.select', fixed: true, width: '40px' },
    { id: 'tags', labelKey: 'images.tags', sortKey: 'tags' },
    { id: 'imageId', labelKey: 'images.imageId', sortKey: 'imageId' },
    { id: 'size', labelKey: 'images.size', sortKey: 'size' },
    { id: 'age', labelKey: 'images.age', sortKey: 'created' },
    { id: 'inUse', labelKey: 'images.inUse', sortKey: 'inUse' },
    { id: 'actions', labelKey: 'images.actions' }
  ];

  const defaultColumnOrder = imageColumns.map(c => c.id);
  const columnPrefs = createColumnPreferences('images', defaultColumnOrder);

  type SortKey = 'tags' | 'imageId' | 'size' | 'created' | 'inUse';
  type SortDir = 'asc' | 'desc';

  let filters = $state({
    search: '',
    sortKey: 'tags' as SortKey,
    sortDir: 'asc' as SortDir
  });

  // date-fns locale for relative ages
  const localeMap: Record<string, typeof enUS> = { en: enUS, fr, es };
  const currentLocale = $derived(localeMap[$localeStore] || enUS);

  const queryClient = useQueryClient();

  // SSE-Query bridge invalidates ['images'] on ImagesChanged events.
  const imagesQuery = createQuery(() => ({
    queryKey: ['images'],
    queryFn: () => imagesApi.list(),
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    staleTime: 0,
  }));

  // --- Selection (only non-self, non-in-use images are selectable) ---
  let selected = $state<Set<string>>(new Set());

  function isSelectable(image: Image): boolean {
    return !image.isSelf && image.inUseBy.length === 0;
  }

  const selectableImages = $derived((imagesQuery.data ?? []).filter(isSelectable));
  const selectedImages = $derived((imagesQuery.data ?? []).filter((i: Image) => selected.has(i.id)));
  const allSelectableSelected = $derived(
    selectableImages.length > 0 && selectableImages.every((i: Image) => selected.has(i.id))
  );

  function toggleSelect(id: string) {
    if (selected.has(id)) selected.delete(id);
    else selected.add(id);
    selected = new Set(selected);
  }

  function toggleSelectAll() {
    if (allSelectableSelected) selected = new Set();
    else selected = new Set(selectableImages.map((i: Image) => i.id));
  }

  // --- Dialogs ---
  let bulkDialogOpen = $state(false);

  let confirmDialog = $state({
    open: false,
    id: '',
    name: ''
  });

  let pruneDialog = $state({
    open: false,
    danglingOnly: true
  });

  // --- Mutations ---
  const removeMutation = createMutation(() => ({
    mutationFn: ({ id, force }: { id: string; force: boolean }) => imagesApi.remove(id, force),
    onSuccess: () => {
      toast.success($t('images.removeSuccess'));
      queryClient.invalidateQueries({ queryKey: ['images'] });
      confirmDialog = { open: false, id: '', name: '' };
    },
    onError: (error: any) => {
      const code = error.response?.data?.errorCode;
      if (code === 'SELF_IMAGE_PROTECTED') {
        toast.error($t('images.selfProtected'));
      } else if (code === 'IMAGE_IN_USE') {
        toast.error($t('images.inUseHint'));
      } else {
        toast.error(error.response?.data?.message || $t('images.removeFailed'));
      }
      confirmDialog = { open: false, id: '', name: '' };
    },
  }));

  const pruneMutation = createMutation(() => ({
    mutationFn: (danglingOnly: boolean) => imagesApi.prune(danglingOnly),
    onSuccess: (data: PruneImagesResult) => {
      toast.success($t('images.pruneSuccess', { n: data.imagesDeleted.length, size: formatBytes(data.spaceReclaimed) }));
      queryClient.invalidateQueries({ queryKey: ['images'] });
      pruneDialog.open = false;
    },
    onError: (error: any) => {
      toast.error(error.response?.data?.message || $t('images.pruneFailed'));
      pruneDialog.open = false;
    },
  }));

  // --- Filter + sort ---
  function imageTag(image: Image): string {
    return image.repoTags.length > 0 ? image.repoTags[0] : '';
  }

  const filteredAndSorted = $derived.by(() => {
    const search = filters.search.toLowerCase();
    const filtered = (imagesQuery.data ?? []).filter((img: Image) =>
      img.repoTags.some(tg => tg.toLowerCase().includes(search)) ||
      img.id.toLowerCase().includes(search)
    );

    const dir = filters.sortDir === 'asc' ? 1 : -1;
    return [...filtered].sort((a: Image, b: Image) => {
      switch (filters.sortKey) {
        case 'size':
          return (a.size - b.size) * dir;
        case 'inUse':
          return (a.inUseBy.length - b.inUseBy.length) * dir;
        case 'created':
          return (new Date(a.created).getTime() - new Date(b.created).getTime()) * dir;
        case 'imageId':
          return a.id.localeCompare(b.id) * dir;
        case 'tags':
        default:
          return imageTag(a).localeCompare(imageTag(b)) * dir;
      }
    });
  });

  function toggleSort(key: string) {
    if (filters.sortKey === key) {
      filters.sortDir = filters.sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      filters.sortKey = key as SortKey;
      filters.sortDir = 'asc';
    }
  }

  function handleColumnReorder(fromIndex: number, toIndex: number) {
    columnPrefs.moveColumn(fromIndex, toIndex);
  }

  function shortId(id: string): string {
    return id.replace('sha256:', '').substring(0, 12);
  }

  function openDelete(image: Image) {
    confirmDialog = {
      open: true,
      id: image.id,
      name: image.repoTags[0] ?? shortId(image.id)
    };
  }

  function deleteTitle(image: Image): string {
    if (image.isSelf) return $t('images.selfProtected');
    if (image.inUseBy.length > 0) return $t('images.inUseHint');
    return $t('images.remove');
  }

  function handleRemove() {
    // In-use images are not deletable from the UI (Docker refuses even with force);
    // only deletable images reach here, so never force.
    removeMutation.mutate({ id: confirmDialog.id, force: false });
  }

  function closeBulkDialog() {
    bulkDialogOpen = false;
    selected = new Set();
  }
</script>

<div class="space-y-4">
  {#if imagesQuery.isLoading}
    <LoadingState message={$t('common.loading')} />
  {:else if imagesQuery.error}
    <div class="text-center py-8 text-red-500">
      {$t('errors.failedToLoad')}: {imagesQuery.error.message}
    </div>
  {:else}
    <!-- Page Header -->
    <div class="mb-2">
      <div class="flex flex-col items-start justify-between gap-3 sm:flex-row sm:items-center">
        <div class="min-w-0">
          <h1 class="text-2xl font-bold text-gray-900 dark:text-white mb-1">{$t('images.title')}</h1>
          <p class="text-base text-gray-600 dark:text-gray-400">
            {$t('images.subtitle')}
          </p>
        </div>
        <div class="flex w-full items-center gap-2 sm:w-auto">
          {#if selected.size > 0}
            <button
              onclick={() => bulkDialogOpen = true}
              class="flex min-h-10 flex-1 items-center justify-center gap-2 px-3 py-1 text-xs font-medium text-white bg-red-600 hover:bg-red-700 rounded-lg transition-colors cursor-pointer sm:min-h-0 sm:flex-none"
            >
              <Trash2 class="w-3 h-3" />
              {$t('images.deleteSelected', { n: selected.size })}
            </button>
          {/if}
          <button
            onclick={() => pruneDialog = { open: true, danglingOnly: true }}
            class="flex min-h-10 flex-1 items-center justify-center gap-2 px-3 py-1 text-xs font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-800 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors cursor-pointer sm:min-h-0 sm:flex-none"
          >
            <Trash2 class="w-3 h-3" />
            {$t('images.prune')}
          </button>
        </div>
      </div>
    </div>

    <!-- Search Bar -->
    <div class="relative">
      <Search class="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-gray-400" />
      <Input
        type="text"
        placeholder={$t('common.search')}
        bind:value={filters.search}
        onkeydown={(e) => e.key === 'Escape' && (filters.search = '')}
        class="pl-10"
      />
    </div>

    {#if !imagesQuery.data || imagesQuery.data.length === 0}
      <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
        <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
          <HardDrive class="w-8 h-8 text-gray-400" />
        </div>
        <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
          {$t('images.noImages')}
        </h3>
        <p class="text-sm text-gray-600 dark:text-gray-400">
          {$t('images.subtitle')}
        </p>
      </div>
    {:else if filteredAndSorted.length === 0}
      <div class="text-center py-12 bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 shadow-lg">
        <div class="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-700 mb-3">
          <Search class="w-8 h-8 text-gray-400" />
        </div>
        <h3 class="text-lg font-semibold text-gray-900 dark:text-white mb-2">
          {$t('images.noImagesMatch')}
        </h3>
        <p class="text-sm text-gray-600 dark:text-gray-400">
          {$t('common.tryAdjustingSearch')}
        </p>
      </div>
    {:else}
      <!-- Compact mobile list: one shared surface, one row per image. -->
      <div class="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm dark:border-gray-700 dark:bg-gray-800 md:hidden">
        {#each filteredAndSorted as image (image.id)}
          {@const isSelected = selected.has(image.id)}
          {@const selectable = isSelectable(image)}
          <article
            class="grid min-h-16 grid-cols-[2.5rem_minmax(0,1fr)_2.5rem] items-center gap-1 border-b border-gray-200 py-1.5 pr-2 pl-1 last:border-b-0 dark:border-gray-700 {isSelected ? 'bg-blue-50 dark:bg-blue-950/30' : ''}"
          >
            <button
              type="button"
              role="checkbox"
              aria-checked={isSelected}
              aria-label={$t('images.select')}
              disabled={!selectable}
              title={selectable ? '' : $t('images.notSelectableHint')}
              onclick={() => selectable && toggleSelect(image.id)}
              class="flex h-10 w-10 items-center justify-center rounded-lg disabled:cursor-not-allowed disabled:opacity-40"
            >
              <span
                class="flex h-5 w-5 items-center justify-center rounded border {isSelected
                  ? 'border-primary bg-primary'
                  : 'border-gray-300 dark:border-gray-600'}"
              >
                {#if isSelected}
                  <Check class="h-3.5 w-3.5 text-white" />
                {/if}
              </span>
            </button>

            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-1">
                {#if image.repoTags.length > 0}
                  {#each image.repoTags as tag}
                    <span class="max-w-full truncate text-sm font-medium text-gray-900 dark:text-gray-100" title={tag}>{tag}</span>
                  {/each}
                {:else}
                  <Badge variant="warning">{$t('images.dangling')}</Badge>
                {/if}
                {#if image.isSelf}
                  <Badge variant="outline">{$t('images.self')}</Badge>
                {/if}
              </div>
              <div class="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-gray-500 dark:text-gray-400">
                <span class="font-mono">{shortId(image.id)}</span>
                <span>·</span>
                <span class="text-gray-700 dark:text-gray-300">{formatBytes(image.size)}</span>
                <span>·</span>
                <span>{formatDistanceToNow(new Date(image.created), { addSuffix: true, locale: currentLocale })}</span>
              </div>
              <div class="mt-1">
                {#if image.inUseBy.length > 0}
                  <Badge variant="secondary" title={image.inUseBy.join(', ')}>
                    {$t('images.usedByCount', { n: image.inUseBy.length })}
                  </Badge>
                {:else}
                  <span class="text-[11px] text-gray-400">{$t('images.unused')}</span>
                {/if}
              </div>
            </div>

            <button
              type="button"
              class="flex h-10 w-10 items-center justify-center rounded-lg text-red-600 transition-colors hover:bg-red-50 disabled:cursor-not-allowed disabled:text-gray-300 disabled:hover:bg-transparent dark:text-red-400 dark:hover:bg-red-950/30 dark:disabled:text-gray-600"
              title={deleteTitle(image)}
              aria-label="{deleteTitle(image)} · {image.repoTags[0] ?? shortId(image.id)}"
              disabled={image.isSelf || image.inUseBy.length > 0 || removeMutation.isPending}
              onclick={() => openDelete(image)}
            >
              <Trash2 class="h-4 w-4" />
            </button>
          </article>
        {/each}
      </div>

      <div class="hidden bg-linear-to-br from-white to-gray-50 dark:from-gray-800 dark:to-gray-900 rounded-xl border border-gray-200 dark:border-gray-700 overflow-visible shadow hover:shadow-lg transition-all duration-300 md:block">
        <div class="overflow-x-auto">
          <table class="w-full">
            <DraggableTableHeader
              columns={imageColumns}
              columnOrder={columnPrefs.order}
              sortKey={filters.sortKey}
              sortDir={filters.sortDir}
              onSort={toggleSort}
              onReorder={handleColumnReorder}
            />
            <tbody class="divide-y divide-gray-100 dark:divide-gray-700">
              {#each filteredAndSorted as image (image.id)}
                <tr class="hover:bg-white dark:hover:bg-gray-800 transition-all">
                  {#each columnPrefs.order as colId (colId)}
                    {#if colId === 'select'}
                      <td class="px-4 py-2">
                        <Checkbox
                          checked={selected.has(image.id)}
                          disabled={!isSelectable(image)}
                          title={isSelectable(image) ? '' : $t('images.notSelectableHint')}
                          onclick={() => isSelectable(image) && toggleSelect(image.id)}
                        />
                      </td>
                    {:else if colId === 'tags'}
                      <td class="px-4 py-2">
                        <div class="flex flex-wrap items-center gap-1">
                          {#if image.repoTags.length > 0}
                            {#each image.repoTags as tag}
                              <span class="text-xs font-medium text-gray-900 dark:text-gray-200">{tag}</span>
                            {/each}
                          {:else}
                            <Badge variant="warning">{$t('images.dangling')}</Badge>
                          {/if}
                          {#if image.isSelf}
                            <Badge variant="outline">{$t('images.self')}</Badge>
                          {/if}
                        </div>
                      </td>
                    {:else if colId === 'imageId'}
                      <td class="px-4 py-2">
                        <div class="text-[10px] text-gray-500 dark:text-gray-400 font-mono">
                          {shortId(image.id)}
                        </div>
                      </td>
                    {:else if colId === 'size'}
                      <td class="px-4 py-2 whitespace-nowrap">
                        <div class="text-xs text-gray-900 dark:text-gray-300">{formatBytes(image.size)}</div>
                      </td>
                    {:else if colId === 'age'}
                      <td class="px-4 py-2 whitespace-nowrap">
                        <div class="text-xs text-gray-500 dark:text-gray-400">
                          {formatDistanceToNow(new Date(image.created), { addSuffix: true, locale: currentLocale })}
                        </div>
                      </td>
                    {:else if colId === 'inUse'}
                      <td class="px-4 py-2">
                        {#if image.inUseBy.length > 0}
                          <Badge variant="secondary" title={image.inUseBy.join(', ')}>
                            {$t('images.usedByCount', { n: image.inUseBy.length })}
                          </Badge>
                        {:else}
                          <span class="text-xs text-gray-400">{$t('images.unused')}</span>
                        {/if}
                      </td>
                    {:else if colId === 'actions'}
                      <td class="px-4 py-2 whitespace-nowrap text-xs">
                        <div class="flex items-center gap-1">
                          <ActionButton
                            icon={Trash2}
                            variant="remove"
                            title={deleteTitle(image)}
                            disabled={image.isSelf || image.inUseBy.length > 0 || removeMutation.isPending}
                            onclick={() => openDelete(image)}
                          />
                        </div>
                      </td>
                    {/if}
                  {/each}
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </div>

      <!-- Select-all helper -->
      {#if selectableImages.length > 0}
        <div class="flex items-center gap-3 text-xs text-gray-500 dark:text-gray-400">
          <Button variant="outline" class="h-9 px-3 text-xs sm:h-7 sm:px-2" onclick={toggleSelectAll}>
            {allSelectableSelected ? $t('common.deselectAll') : $t('common.selectAll')}
          </Button>
          {#if selected.size > 0}
            <span>{$t('images.imagesSelected', { n: selected.size })}</span>
          {/if}
        </div>
      {/if}
    {/if}
  {/if}

  <!-- Single delete confirm -->
  <ConfirmDialog
    open={confirmDialog.open}
    title={$t('images.confirmRemove')}
    description={$t('images.confirmRemoveWithName', { name: confirmDialog.name })}
    confirmDisabled={removeMutation.isPending}
    onconfirm={handleRemove}
    oncancel={() => confirmDialog.open = false}
  />

  <!-- Prune confirm -->
  <ConfirmDialog
    open={pruneDialog.open}
    title={$t('images.pruneTitle')}
    description={$t('images.pruneDescription')}
    confirmText={$t('images.prune')}
    onconfirm={() => pruneMutation.mutate(pruneDialog.danglingOnly)}
    oncancel={() => pruneDialog.open = false}
  >
    <label class="mt-4 flex items-center gap-2 cursor-pointer">
      <Checkbox
        checked={pruneDialog.danglingOnly}
        onclick={() => pruneDialog.danglingOnly = !pruneDialog.danglingOnly}
      />
      <div class="flex flex-col">
        <span class="text-sm text-gray-700 dark:text-gray-300">{$t('images.pruneDanglingOnly')}</span>
        <span class="text-xs text-gray-500 dark:text-gray-400">{$t('images.pruneDanglingOnlyHint')}</span>
      </div>
    </label>
  </ConfirmDialog>

  <!-- Bulk delete -->
  <BulkImageDeleteDialog
    open={bulkDialogOpen}
    images={selectedImages}
    onClose={closeBulkDialog}
  />
</div>
