<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import MainLayout from '$lib/components/layout/MainLayout.svelte';
	import MaintenanceOverlay from '$lib/components/update/MaintenanceOverlay.svelte';
	import * as auth from '$lib/stores/auth.svelte';
	import { isAdmin } from '$lib/stores/auth.svelte';
	import { authApi, oidcApi } from '$lib/api';
	import { page } from '$app/state';
	import { replaceState } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { t } from '$lib/i18n';
	import { readLinkOutcome } from '$lib/utils/oidc';
	import { initializeSSEConnection, stopSSEConnection, onMaintenanceMode, onProjectUpdatesChecked, onContainerUpdatesChecked, onOperationUpdate, onReconnected } from '$lib/stores/sse.svelte';
	import { setupSSEQueryBridge } from '$lib/services/sseQueryBridge';
	import { enterMaintenanceMode, startPeriodicCheck, stopPeriodicCheck } from '$lib/stores/update.svelte';
	import { handleProjectUpdatesCheckedEvent, loadCachedUpdateStatus } from '$lib/stores/projectUpdate.svelte';
	import { handleContainerUpdatesCheckedEvent, loadCachedContainerUpdateStatus } from '$lib/stores/containerUpdate.svelte';
	import { loadInitial as loadActionLog, loadLastByEntity, handleOperationUpdate, reconcileAfterReconnect } from '$lib/stores/actionLog.svelte';
	import { getQueryClient } from '$lib/queryClient';

	let { children } = $props();
	let isLoading = $state(true);
	let cleanupBridge: (() => void) | null = null;
	let cleanupMaintenanceListener: (() => void) | null = null;
	let cleanupProjectUpdatesListener: (() => void) | null = null;
	let cleanupContainerUpdatesListener: (() => void) | null = null;
	let cleanupOperationListener: (() => void) | null = null;
	let cleanupReconnectListener: (() => void) | null = null;

	// Use the singleton QueryClient - same instance used by all components
	const queryClient = getQueryClient();

	/** Back from "Link my SSO account": toast the result, refresh the user, drop the query params. */
	async function reportSsoLinkOutcome() {
		const outcome = readLinkOutcome(page.url.searchParams);
		if (!outcome) return;

		const url = new URL(page.url.href);
		url.searchParams.delete('oidc_linked');
		url.searchParams.delete('oidc_link_error');
		replaceState(url, page.state);

		const { displayName: name } = await oidcApi.getOidcConfig();
		if (outcome.linked) {
			toast.success($t('auth.oidc.link.success', { name }));
			try {
				auth.updateUser(await authApi.getCurrentUser());
			} catch {
				// The badge catches up on the next load.
			}
		} else {
			toast.error($t(`auth.oidc.link.errors.${outcome.error}`, { name }));
		}
	}

	onMount(async () => {
		// Si on a un token mais pas d'utilisateur, récupérer les infos utilisateur
		if (auth.isAuthenticated.current && !auth.auth.user) {
			try {
				const user = await authApi.getCurrentUser();
				auth.updateUser(user);
			} catch (error) {
				// Si l'appel échoue (token invalide), déconnecter
				auth.logout();
				window.location.href = '/login';
				return;
			}
		}

		void reportSsoLinkOutcome();

		// Initialize global SSE connection
		await initializeSSEConnection();

		// Set up the SSE-Query bridge for automatic cache invalidation
		cleanupBridge = setupSSEQueryBridge(queryClient);

		// Subscribe to maintenance mode notifications
		cleanupMaintenanceListener = onMaintenanceMode((notification) => {
			enterMaintenanceMode(notification);
		});

		// Subscribe to operation updates for action log
		cleanupOperationListener = onOperationUpdate((event) => {
			handleOperationUpdate(event);
		});

		// On SSE reconnect, re-pull authoritative operation state. Terminal events can be
		// missed while the stream is down (e.g. the tunnel carrying SSE is recreated by an
		// update), which would otherwise leave spinners stuck on "running".
		cleanupReconnectListener = onReconnected(() => {
			void reconcileAfterReconnect();
		});

		// Load initial action log data
		await loadActionLog();
		await loadLastByEntity();

		// Start periodic update checking for admin users
		if (isAdmin.current) {
			startPeriodicCheck();

			// Subscribe to project updates SSE events (backend-driven periodic checks)
			cleanupProjectUpdatesListener = onProjectUpdatesChecked((event) => {
				handleProjectUpdatesCheckedEvent(event);
			});

			// Subscribe to container updates SSE events
			cleanupContainerUpdatesListener = onContainerUpdatesChecked((event) => {
				handleContainerUpdatesCheckedEvent(event);
			});

			// Load cached update status from backend on initial load
			await loadCachedUpdateStatus();
			await loadCachedContainerUpdateStatus();
		}

		isLoading = false;
	});

	onDestroy(() => {
		// Clean up the bridge when the layout is destroyed
		if (cleanupBridge) {
			cleanupBridge();
		}
		// Clean up maintenance mode listener
		if (cleanupMaintenanceListener) {
			cleanupMaintenanceListener();
		}
		// Clean up project updates listener
		if (cleanupProjectUpdatesListener) {
			cleanupProjectUpdatesListener();
		}
		// Clean up container updates listener
		if (cleanupContainerUpdatesListener) {
			cleanupContainerUpdatesListener();
		}
		// Clean up operation listener
		if (cleanupOperationListener) {
			cleanupOperationListener();
		}
		// Clean up reconnect reconciliation listener
		if (cleanupReconnectListener) {
			cleanupReconnectListener();
		}
		// Stop periodic update checking
		stopPeriodicCheck();
	});
</script>

{#if isLoading}
	<div class="min-h-screen flex items-center justify-center bg-gray-100 dark:bg-gray-900">
		<div class="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
	</div>
{:else}
	<MainLayout>
		{@render children()}
	</MainLayout>
{/if}

<!-- Maintenance Overlay (shown during app updates) -->
<MaintenanceOverlay />
