import axios from 'axios';
import { browser } from '$app/env';
import { refreshTokens as updateAuthTokens } from '$lib/stores/auth.svelte';

// Single source of truth for refreshing the access token.
//
// The refresh token lives in the HttpOnly `lh_refresh` cookie and is rotated on
// every refresh: a second concurrent refresh would send the now-stale cookie and
// fail, logging the user out. Several callers can trigger a refresh at once (the
// axios 401 interceptor, the proactive timer, the SSE connector, multiple tabs
// racing), so all refreshes are funneled through a single in-flight promise. While
// one refresh is pending, every other caller awaits the same result instead of
// firing its own request.

function getApiUrl(): string {
  if (!browser) return '';
  const viteApiUrl = import.meta.env.VITE_API_URL;
  if (viteApiUrl !== undefined && viteApiUrl !== '') {
    return viteApiUrl;
  }
  return '';
}

let inFlight: Promise<string | null> | null = null;

async function performRefresh(): Promise<string | null> {
  try {
    const apiUrl = getApiUrl();
    const refreshUrl = apiUrl ? `${apiUrl}/api/auth/refresh` : '/api/auth/refresh';

    // The refresh token is sent automatically via the HttpOnly cookie (withCredentials);
    // nothing is read from or written to JavaScript-accessible storage for it.
    const response = await axios.post(refreshUrl, {}, { withCredentials: true });

    const accessToken: string | undefined = response.data?.data?.accessToken;
    if (!accessToken) return null;

    localStorage.setItem('accessToken', accessToken);
    updateAuthTokens(accessToken);
    return accessToken;
  } catch {
    return null;
  }
}

/**
 * Refresh the access token, deduplicating concurrent callers onto a single request.
 * Returns the new access token, or null if the refresh failed (caller decides what
 * to do — typically log out). Does not itself log out or redirect.
 */
export function refreshAccessToken(): Promise<string | null> {
  if (!browser) return Promise.resolve(null);

  if (inFlight) return inFlight;

  inFlight = performRefresh().finally(() => {
    inFlight = null;
  });

  return inFlight;
}
