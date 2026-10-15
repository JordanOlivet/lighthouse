<script lang="ts">
	import { page } from '$app/state';
	import { goto, beforeNavigate } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Save, AlertCircle, FileText, Settings2 } from 'lucide-svelte';
	import { composeApi } from '$lib/api';
	import type { ProjectFile, ProjectFileKind, ProjectFilesResponse } from '$lib/types';
	import LoadingState from '$lib/components/common/LoadingState.svelte';
	import ConfirmDialog from '$lib/components/common/ConfirmDialog.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import MonacoEditor from '$lib/components/MonacoEditor.svelte';
	import { t } from '$lib/i18n';
	import { toast } from 'svelte-sonner';

	const projectName = $derived(
		page.params.projectName ? decodeURIComponent(page.params.projectName) : ''
	);

	const queryClient = useQueryClient();

	// Per-tab editing state. `original`/`etag` reflect the last version loaded from (or saved to)
	// the server; `content` is the live editor buffer.
	interface FileState {
		content: string;
		original: string;
		etag: string | null;
		exists: boolean;
	}

	let compose = $state<FileState>({ content: '', original: '', etag: null, exists: false });
	let env = $state<FileState>({ content: '', original: '', etag: null, exists: false });
	let activeTab = $state<ProjectFileKind>('compose');
	let loaded = $state(false);

	// The last query result already applied to the editor. Used to apply only on a genuine data
	// change (new fetch), never on unrelated reactivity — so a post-save state (with the fresh
	// ETag from the PUT response) is not clobbered by re-applying the pre-refetch cached data.
	let lastAppliedData: ProjectFilesResponse | undefined;

	// Set once a successful save happens so the navigation guard doesn't block the post-save redirect.
	let savedNavigation = $state(false);

	let applyDialogOpen = $state(false);
	let conflictDialogOpen = $state(false);

	const composeDirty = $derived(compose.content !== compose.original);
	const envDirty = $derived(env.content !== env.original);
	const isDirty = $derived(composeDirty || envDirty);

	const filesQuery = createQuery(() => ({
		queryKey: ['compose', 'projectFiles', projectName],
		queryFn: () => composeApi.getProjectFiles(projectName),
		enabled: !!projectName,
		// Don't refetch the file under the editor on focus/reconnect: it would replace the buffer
		// (or, right after a save, the fresh ETag) out from under the user. The mount refetch still
		// runs (staleTime 0), which is enough to correct a stale cached ETag.
		refetchOnWindowFocus: false,
		refetchOnReconnect: false
	}));

	function applyLoaded(files: ProjectFile[]) {
		const composeFile = files.find((f) => f.kind === 'compose');
		const envFile = files.find((f) => f.kind === 'env');

		if (composeFile) {
			compose = {
				content: composeFile.content ?? '',
				original: composeFile.content ?? '',
				etag: composeFile.etag,
				exists: composeFile.exists
			};
		}
		if (envFile) {
			env = {
				content: envFile.content ?? '',
				original: envFile.content ?? '',
				etag: envFile.etag,
				exists: envFile.exists
			};
		}
		loaded = true;
	}

	// Apply server data only when a genuinely new fetch arrives (reference change) and the user has
	// no pending edits. This is the single source of the ETag: it self-heals a stale ETag served
	// from a previous visit's TanStack cache (the background refetch brings a new object and it is
	// applied) without clobbering either in-progress edits or the fresh ETag set right after a save
	// (that runs before the invalidation refetch resolves, so the data reference has not changed yet).
	$effect(() => {
		const data = filesQuery.data;
		if (!data || data === lastAppliedData || isDirty) {
			return;
		}
		lastAppliedData = data;
		applyLoaded(data.files);
	});

	function stateFor(kind: ProjectFileKind): FileState {
		return kind === 'compose' ? compose : env;
	}

	const saveMutation = createMutation(() => ({
		mutationFn: async () => {
			// Save every dirty file, compose first so a validation failure surfaces before .env.
			const saved: ProjectFile[] = [];
			if (composeDirty) {
				saved.push(
					await composeApi.updateProjectFile(projectName, {
						kind: 'compose',
						content: compose.content,
						etag: compose.etag
					})
				);
			}
			if (envDirty) {
				saved.push(
					await composeApi.updateProjectFile(projectName, {
						kind: 'env',
						content: env.content,
						etag: env.etag
					})
				);
			}
			return saved;
		},
		onSuccess: (saved: ProjectFile[]) => {
			for (const file of saved) {
				const next: FileState = {
					content: file.content ?? '',
					original: file.content ?? '',
					etag: file.etag,
					exists: file.exists
				};
				if (file.kind === 'compose') compose = next;
				else env = next;
			}
			// Freeze the sync effect on whatever data is currently cached so it can't re-apply a
			// pre-save version over the fresh ETag we just set. The invalidation below brings a new
			// object, which the effect will then apply (identical content, matching ETag).
			lastAppliedData = filesQuery.data;
			queryClient.invalidateQueries({ queryKey: ['compose', 'projectFiles', projectName] });
			queryClient.invalidateQueries({ queryKey: ['compose', 'project', projectName] });
			queryClient.invalidateQueries({ queryKey: ['projectParsedDetails', projectName] });
			toast.success($t('compose.fileSaved'));
			applyDialogOpen = true;
		},
		onError: (error: any) => {
			const code = error.response?.data?.errorCode;
			if (code === 'FILE_MODIFIED') {
				conflictDialogOpen = true;
			} else if (code === 'INVALID_COMPOSE_FILE') {
				toast.error(error.response?.data?.message || $t('compose.invalidComposeFile'));
			} else {
				toast.error(error.response?.data?.message || $t('compose.failedToSave'));
			}
		}
	}));

	const upMutation = createMutation(() => ({
		mutationFn: () => composeApi.upProject(projectName, { detach: true }),
		onSuccess: () => {
			toast.success($t('compose.upSuccess'));
			leaveToDetail();
		},
		onError: () => toast.error($t('compose.failedToLoadProject'))
	}));

	function handleContentChange(kind: ProjectFileKind, value: string) {
		if (kind === 'compose') compose.content = value;
		else env.content = value;
	}

	function leaveToDetail() {
		savedNavigation = true;
		goto(`/compose/projects/${encodeURIComponent(projectName)}`);
	}

	async function reloadFromServer() {
		conflictDialogOpen = false;
		// Force a network refetch, then overwrite the editor with the fresh version (discarding the
		// user's edits in this tab, as the dialog warns). Mark the data as applied so the sync
		// effect does not re-run for the same result.
		const res = await filesQuery.refetch();
		if (res.data) {
			lastAppliedData = res.data;
			applyLoaded(res.data.files);
		}
	}

	// Guard against losing unsaved edits on navigation (link clicks, back button, etc.).
	beforeNavigate(({ cancel }) => {
		if (isDirty && !savedNavigation) {
			if (!confirm($t('compose.unsavedLeaveConfirm'))) {
				cancel();
			}
		}
	});

	const activeState = $derived(stateFor(activeTab));
</script>

<!-- Bounded to the viewport (minus app header + main padding) so the editor fills the
     remaining space instead of overflowing it by a few pixels. -->
<div class="flex flex-col gap-6 h-[calc(100dvh-9.5rem)]">
	<!-- Header -->
	<div class="flex items-center justify-between shrink-0">
		<div class="flex items-center gap-4">
			<a
				href={`/compose/projects/${encodeURIComponent(projectName)}`}
				class="p-2 hover:bg-gray-100 dark:hover:bg-gray-800 rounded-lg transition-colors"
				title={$t('common.back')}
			>
				<ArrowLeft class="w-5 h-5 text-gray-900 dark:text-white" />
			</a>
			<div>
				<h1 class="text-2xl font-bold text-gray-900 dark:text-white">
					{$t('compose.editFile')}
				</h1>
				<p class="text-sm text-gray-500 dark:text-gray-400 font-mono">{projectName}</p>
			</div>
		</div>
		<div class="flex items-center gap-3">
			{#if isDirty}
				<span class="flex items-center gap-2 text-sm text-yellow-600 dark:text-yellow-400">
					<AlertCircle class="w-4 h-4" />
					{$t('compose.unsavedChanges')}
				</span>
			{/if}
			<Button onclick={() => saveMutation.mutate()} disabled={!isDirty || saveMutation.isPending}>
				<Save class="w-4 h-4 mr-2" />
				{saveMutation.isPending ? $t('common.loading') : $t('common.save')}
			</Button>
		</div>
	</div>

	{#if filesQuery.isLoading || !loaded}
		<LoadingState message={$t('compose.loadingDetails')} />
	{:else if filesQuery.error}
		<div class="text-center py-8">
			<p class="text-red-500">{$t('compose.failedToLoadFile')}</p>
			<Button variant="outline" class="mt-4" onclick={leaveToDetail}>
				{$t('common.back')}
			</Button>
		</div>
	{:else}
		<div class="flex flex-col flex-1 min-h-0 gap-4">
		<!-- Tabs -->
		<div class="flex items-center gap-1 border-b border-gray-200 dark:border-gray-700 shrink-0">
			<button
				onclick={() => (activeTab = 'compose')}
				class="flex items-center gap-2 px-4 py-2 text-sm font-medium border-b-2 transition-colors cursor-pointer {activeTab ===
				'compose'
					? 'border-blue-600 text-blue-600 dark:text-blue-400'
					: 'border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200'}"
			>
				<FileText class="w-4 h-4" />
				{compose.exists ? 'docker-compose.yml' : $t('compose.composeTab')}
				{#if composeDirty}<span class="w-1.5 h-1.5 rounded-full bg-yellow-500"></span>{/if}
			</button>
			<button
				onclick={() => (activeTab = 'env')}
				class="flex items-center gap-2 px-4 py-2 text-sm font-medium border-b-2 transition-colors cursor-pointer {activeTab ===
				'env'
					? 'border-blue-600 text-blue-600 dark:text-blue-400'
					: 'border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200'}"
			>
				<Settings2 class="w-4 h-4" />
				.env
				{#if !env.exists}
					<span class="text-xs text-gray-400 dark:text-gray-500">({$t('compose.envNew')})</span>
				{/if}
				{#if envDirty}<span class="w-1.5 h-1.5 rounded-full bg-yellow-500"></span>{/if}
			</button>
		</div>

		<!-- Editor Info Bar -->
		<div
			class="flex items-center justify-between px-4 py-2 bg-gray-100 dark:bg-gray-800 rounded-t-lg text-sm shrink-0"
		>
			<div class="flex items-center gap-4 text-gray-600 dark:text-gray-400">
				<span>{activeTab === 'compose' ? $t('compose.yaml') : '.env'}</span>
				<span>|</span>
				<span>{$t('compose.utf8')}</span>
			</div>
			<div class="flex items-center gap-4 text-gray-600 dark:text-gray-400">
				<span>{activeState.content.split('\n').length} {$t('compose.lines')}</span>
				<span>{activeState.content.length} {$t('compose.characters')}</span>
			</div>
		</div>

		<!-- One Monaco instance per tab, mounted only when active so each keeps its own model. -->
		{#if activeTab === 'compose'}
			<MonacoEditor
				value={compose.content}
				language="yaml"
				onchange={(v) => handleContentChange('compose', v)}
				class="flex-1 min-h-0"
			/>
		{:else}
			<MonacoEditor
				value={env.content}
				language="ini"
				onchange={(v) => handleContentChange('env', v)}
				class="flex-1 min-h-0"
			/>
		{/if}
		</div>
	{/if}
</div>

<!-- Apply-now dialog shown after a successful save -->
<ConfirmDialog
	open={applyDialogOpen}
	title={$t('compose.applyTitle')}
	description={$t('compose.applyDescription')}
	confirmText={$t('compose.applyNow')}
	cancelText={$t('compose.applyLater')}
	confirmVariant="default"
	confirmDisabled={upMutation.isPending}
	onconfirm={() => {
		applyDialogOpen = false;
		upMutation.mutate();
	}}
	oncancel={() => {
		applyDialogOpen = false;
		leaveToDetail();
	}}
/>

<!-- Conflict dialog shown when the file changed underneath the editor -->
<ConfirmDialog
	open={conflictDialogOpen}
	title={$t('compose.conflictTitle')}
	description={$t('compose.conflictDescription')}
	confirmText={$t('compose.conflictReload')}
	cancelText={$t('common.cancel')}
	confirmVariant="default"
	onconfirm={reloadFromServer}
	oncancel={() => (conflictDialogOpen = false)}
/>
