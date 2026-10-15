import { browser } from '$app/env';
import type { User } from '$lib/types';
import { getRoleFromToken } from '$lib/utils/jwt';

// Svelte 5 pattern: export state object with properties (not individual $state variables)
//
// Only the short-lived access token is held client-side. The refresh token lives
// exclusively in an HttpOnly cookie (`lh_refresh`) set by the backend and is never
// readable by JavaScript — this is the defense against token theft via XSS.
export const auth = $state({
  user: null as User | null,
  accessToken: browser ? localStorage.getItem('accessToken') : null
});

// Derived state as getters - Svelte 5 doesn't allow exporting $derived
export const isAuthenticated = {
  get current() { return !!auth.accessToken; }
};
// Prefer the loaded user's role; before /me resolves (e.g. right after a reload) fall
// back to the role embedded in the access token so admin UI does not flicker.
export const isAdmin = {
  get current() {
    const role = auth.user?.role ?? getRoleFromToken(auth.accessToken);
    return role?.toLowerCase() === 'admin';
  }
};

// Actions
export function login(newAccessToken: string, newUser: User) {
  if (browser) {
    localStorage.setItem('accessToken', newAccessToken);
  }
  auth.user = newUser;
  auth.accessToken = newAccessToken;
}

export function logout() {
  if (browser) {
    localStorage.removeItem('accessToken');
    // Clean up any legacy refresh token left over from a previous version.
    localStorage.removeItem('refreshToken');
  }
  auth.user = null;
  auth.accessToken = null;
}

export function updateUser(newUser: User) {
  auth.user = newUser;
}

export function refreshTokens(newAccessToken: string) {
  if (browser) {
    localStorage.setItem('accessToken', newAccessToken);
  }
  auth.accessToken = newAccessToken;
}
