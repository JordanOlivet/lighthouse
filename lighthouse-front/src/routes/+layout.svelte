<script lang="ts">
	import '../app.css';
	import { QueryClientProvider } from '@tanstack/svelte-query';
	import { Toaster } from 'svelte-sonner';
	import { getQueryClient } from '$lib/queryClient';
	import { t } from '$lib/i18n';

	let { children } = $props();

	// Use singleton QueryClient instance
	const queryClient = getQueryClient();

	// No need for theme initialization - theme.svelte.ts handles it automatically
</script>

<svelte:head>
	<link rel="icon" href="/favicon.svg" />
	<title>Lighthouse</title>
</svelte:head>

<QueryClientProvider client={queryClient}>
	{@render children()}
	<Toaster richColors closeButton closeButtonAriaLabel={$t('common.close')} position="top-right" />
</QueryClientProvider>
