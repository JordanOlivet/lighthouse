import { browser } from '$app/env';
import configApi from '$lib/api/config';
import { logger } from '$lib/utils/logger';

export const AUTO_UPDATE_KEYS = {
  composeEnabled: 'AutoUpdateComposeEnabled',
  composeCron: 'AutoUpdateComposeCron',
  appEnabled: 'AutoUpdateAppEnabled',
  appCron: 'AutoUpdateAppCron'
} as const;

const DEFAULT_CRON = '0 2 * * *';

export const autoUpdateState = $state({
  composeEnabled: false,
  composeCron: DEFAULT_CRON,
  appEnabled: false,
  appCron: DEFAULT_CRON,
  loaded: false
});

function parseBool(value: string | undefined): boolean {
  return value?.toLowerCase() === 'true';
}

export async function loadAutoUpdateSettings(): Promise<void> {
  if (!browser) return;

  try {
    const settings = await configApi.getSettings();
    autoUpdateState.composeEnabled = parseBool(settings[AUTO_UPDATE_KEYS.composeEnabled]);
    autoUpdateState.composeCron = settings[AUTO_UPDATE_KEYS.composeCron] || DEFAULT_CRON;
    autoUpdateState.appEnabled = parseBool(settings[AUTO_UPDATE_KEYS.appEnabled]);
    autoUpdateState.appCron = settings[AUTO_UPDATE_KEYS.appCron] || DEFAULT_CRON;
    autoUpdateState.loaded = true;
  } catch (error) {
    logger.error('[Auto Update Store] Failed to load settings:', error);
  }
}

export async function saveAutoUpdateSetting(key: string, value: string): Promise<{ ok: boolean; error?: string }> {
  if (!browser) return { ok: false };
  try {
    await configApi.updateSetting(key, { value });
    return { ok: true };
  } catch (error: unknown) {
    let message: string | undefined;
    if (error && typeof error === 'object' && 'response' in error) {
      const axiosError = error as { response?: { data?: { message?: string } } };
      message = axiosError.response?.data?.message;
    }
    if (!message && error instanceof Error) {
      message = error.message;
    }
    logger.error(`[Auto Update Store] Failed to save ${key}:`, message ?? error);
    return { ok: false, error: message };
  }
}
