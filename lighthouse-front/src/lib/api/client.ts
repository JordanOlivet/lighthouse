import axios, { AxiosError } from 'axios';
import type { InternalAxiosRequestConfig } from 'axios';
import * as auth from '$lib/stores/auth.svelte';
import { reconnectSSEWithNewToken } from '$lib/stores/sse.svelte';
import { refreshAccessToken } from '$lib/api/tokenRefresh';
import { isNearExpiration } from '$lib/utils/jwt';
import { browser } from '$app/env';

// Extend InternalAxiosRequestConfig to include _retry property for token refresh
interface RetryableRequestConfig extends InternalAxiosRequestConfig {
  _retry?: boolean;
  _retryCount?: number;
}

// Use relative URL by default so requests are proxied (Vite proxy in dev, nginx in prod).
// Override by setting VITE_API_URL (e.g. in .env.development.local for a remote backend).
const getApiUrl = () => {
  if (!browser) return '';

  const viteApiUrl = import.meta.env.VITE_API_URL;
  if (viteApiUrl !== undefined && viteApiUrl !== '') {
    return viteApiUrl;
  }
  return '';
};

const API_URL = getApiUrl();

// Refresh proactively when less than 10 minutes remain on the access token.
const PROACTIVE_REFRESH_MARGIN_MS = 10 * 60 * 1000;

function isTokenNearExpiration(token: string): boolean {
  return isNearExpiration(token, PROACTIVE_REFRESH_MARGIN_MS);
}

/**
 * Proactively refresh the access token if it's near expiration.
 * Returns true if refresh was successful or not needed, false on failure.
 */
async function proactiveTokenRefresh(): Promise<boolean> {
  if (!browser) return true;

  const token = localStorage.getItem('accessToken');
  if (!token) return true; // No token, nothing to refresh

  if (!isTokenNearExpiration(token)) {
    return true; // Token is still valid, no refresh needed
  }

  // Shared, deduplicated refresh (see tokenRefresh.ts). Concurrent callers reuse one request.
  const accessToken = await refreshAccessToken();
  if (!accessToken) {
    // If refresh fails, log out the user
    auth.logout();
    window.location.href = '/login';
    return false;
  }

  // Reconnect SSE with the new token
  reconnectSSEWithNewToken();
  return true;
}

// Proactive token refresh: check every 60 seconds
if (browser) {
  setInterval(() => {
    proactiveTokenRefresh();
  }, 60_000);

  // Refresh token when user returns to the tab after being away
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      proactiveTokenRefresh();
    }
  });
}

export const apiClient = axios.create({
  baseURL: API_URL ? `${API_URL}/api` : '/api',
  headers: {
    'Content-Type': 'application/json',
  },
  // Send the HttpOnly refresh-token cookie on auth requests (login/refresh/logout).
  withCredentials: true,
});

// Request interceptor to add auth token
apiClient.interceptors.request.use(
  (config) => {
    if (!browser) return config;
    
    const token = localStorage.getItem('accessToken');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

// Response interceptor to handle token refresh
apiClient.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    if (!browser) return Promise.reject(error);
    
    const originalRequest = error.config as RetryableRequestConfig;

    // Don't attempt token refresh for auth endpoints
    const isAuthEndpoint = originalRequest?.url?.includes('/auth/login') ||
                          originalRequest?.url?.includes('/auth/refresh');

    if (error.response?.status === 401 && !isAuthEndpoint) {
      originalRequest._retryCount = (originalRequest._retryCount || 0) + 1;
      if (originalRequest._retryCount > 2) {
        // Trop de tentatives, logout
        auth.logout();
        window.location.href = '/login';
        return Promise.reject(error);
      }

      // Shared, deduplicated refresh (see tokenRefresh.ts): parallel 401s across
      // requests/tabs reuse a single refresh call and one cookie rotation.
      const accessToken = await refreshAccessToken();
      if (!accessToken) {
        // Refresh failed, logout user via store
        auth.logout();
        window.location.href = '/login';
        return Promise.reject(error);
      }

      // Reconnect SSE with the new token
      reconnectSSEWithNewToken();

      originalRequest.headers.Authorization = `Bearer ${accessToken}`;
      return apiClient(originalRequest);
    }

    return Promise.reject(error);
  }
);
