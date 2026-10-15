import { redirect } from '@sveltejs/kit';
import { browser } from '$app/env';
import type { PageLoad } from './$types';

export const load: PageLoad = async () => {
  // Redirect to login or dashboard based on auth status
  if (browser) {
    const accessToken = localStorage.getItem('accessToken');
    if (accessToken) {
      throw redirect(302, '/dashboard');
    }
  }
  throw redirect(302, '/login');
};
