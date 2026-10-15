import { browser } from '$app/env';
import configApi from '$lib/api/config';
import { logger } from '$lib/utils/logger';

export const AUTO_PRUNE_KEYS = {
  enabled: 'AutoPruneImagesEnabled',
  cron: 'AutoPruneImagesCron',
  danglingOnly: 'AutoPruneImagesDanglingOnly'
} as const;

const DEFAULT_CRON = '0 3 * * *';

export const autoPruneState = $state({
  enabled: false,
  cron: DEFAULT_CRON,
  danglingOnly: true,
  loaded: false
});

function parseBool(value: string | undefined, fallback = false): boolean {
  if (value === undefined) return fallback;
  return value.toLowerCase() === 'true';
}

export async function loadAutoPruneSettings(): Promise<void> {
  if (!browser) return;

  try {
    const settings = await configApi.getSettings();
    autoPruneState.enabled = parseBool(settings[AUTO_PRUNE_KEYS.enabled]);
    autoPruneState.cron = settings[AUTO_PRUNE_KEYS.cron] || DEFAULT_CRON;
    // Absent setting defaults to dangling-only (safe), matching the backend.
    autoPruneState.danglingOnly = parseBool(settings[AUTO_PRUNE_KEYS.danglingOnly], true);
    autoPruneState.loaded = true;
  } catch (error) {
    logger.error('[Auto Prune Store] Failed to load settings:', error);
  }
}
