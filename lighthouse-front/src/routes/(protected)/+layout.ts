import { browser } from '$app/env';
import { redirect } from '@sveltejs/kit';
import type { LayoutLoad } from './$types';

export const prerender = false;

export const load: LayoutLoad = async () => {
	if (browser) {
		const accessToken = localStorage.getItem('accessToken');

		if (!accessToken) {
			throw redirect(302, '/login');
		}
	}

	return {};
};
