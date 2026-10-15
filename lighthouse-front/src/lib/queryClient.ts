import { QueryClient } from '@tanstack/svelte-query';
import { browser } from '$app/env';
import { logger } from './utils/logger';

// Singleton QueryClient instance shared across the entire app
let queryClientInstance: QueryClient | null = null;

export function getQueryClient(): QueryClient {
  if (!queryClientInstance) {
    queryClientInstance = new QueryClient({
      defaultOptions: {
        queries: {
          staleTime: 1000 * 60 * 5, // 5 minutes
          gcTime: 1000 * 60 * 30, // 30 minutes
          retry: 1,
          refetchOnWindowFocus: false,
        },
      },
    });

    if (browser) {
      logger.log('[QueryClient] Created singleton instance');
    }
  }
  return queryClientInstance;
}

// For debugging - get the current instance without creating
export function getQueryClientInstance(): QueryClient | null {
  return queryClientInstance;
}
