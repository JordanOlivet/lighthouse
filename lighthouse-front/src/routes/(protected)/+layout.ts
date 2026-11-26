import { browser } from '$app/env';
import { redirect } from '@sveltejs/kit';
import type { LayoutLoad } from './$types';

export const prerender = false;

export const load: LayoutLoad = async ({ url }) => {
	if (browser) {
		const accessToken = localStorage.getItem('accessToken');

		if (!accessToken) {
			// Keep the deep link so the user lands back here after (SSO) login.
			throw redirect(302, '/login?returnUrl=' + encodeURIComponent(url.pathname + url.search));
		}
	}

	return {};
};
