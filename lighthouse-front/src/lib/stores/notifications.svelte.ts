import { browser } from '$app/env';
import configApi from '$lib/api/config';
import notificationsApi from '$lib/api/notifications';
import { logger } from '$lib/utils/logger';

export const NOTIF_KEYS = {
  discordEnabled: 'NotificationsDiscordEnabled',
  discordWebhookUrl: 'NotificationsDiscordWebhookUrl',
  webhookEnabled: 'NotificationsWebhookEnabled',
  webhookUrl: 'NotificationsWebhookUrl',
  webhookHeaders: 'NotificationsWebhookHeaders'
} as const;

export const notificationsState = $state({
  discordEnabled: false,
  // Masked value returned by the API. Treated as write-only: only persisted when
  // the user types a new value (see `webhookDirty`).
  discordWebhookUrl: '',
  webhookEnabled: false,
  webhookUrl: '',
  // Masked ("••••••••") when configured; write-only like the Discord URL.
  webhookHeaders: '',
  loaded: false
});

function parseBool(value: string | undefined): boolean {
  return value?.toLowerCase() === 'true';
}

export async function loadNotificationSettings(): Promise<void> {
  if (!browser) return;

  try {
    const settings = await configApi.getSettings();
    notificationsState.discordEnabled = parseBool(settings[NOTIF_KEYS.discordEnabled]);
    notificationsState.discordWebhookUrl = settings[NOTIF_KEYS.discordWebhookUrl] || '';
    notificationsState.webhookEnabled = parseBool(settings[NOTIF_KEYS.webhookEnabled]);
    notificationsState.webhookUrl = settings[NOTIF_KEYS.webhookUrl] || '';
    notificationsState.webhookHeaders = settings[NOTIF_KEYS.webhookHeaders] || '';
    notificationsState.loaded = true;
  } catch (error) {
    logger.error('[Notifications Store] Failed to load settings:', error);
  }
}

function extractErrorMessage(error: unknown): string | undefined {
  let message: string | undefined;
  if (error && typeof error === 'object' && 'response' in error) {
    const axiosError = error as { response?: { data?: { message?: string } } };
    message = axiosError.response?.data?.message;
  }
  if (!message && error instanceof Error) {
    message = error.message;
  }
  return message;
}

export async function saveNotificationSetting(
  key: string,
  value: string
): Promise<{ ok: boolean; error?: string }> {
  if (!browser) return { ok: false };
  try {
    await configApi.updateSetting(key, { value });
    return { ok: true };
  } catch (error: unknown) {
    const message = extractErrorMessage(error);
    logger.error(`[Notifications Store] Failed to save ${key}:`, message ?? error);
    return { ok: false, error: message };
  }
}

export async function testDiscordWebhook(
  webhookUrl?: string
): Promise<{ ok: boolean; error?: string }> {
  if (!browser) return { ok: false };
  try {
    await notificationsApi.testDiscord(webhookUrl);
    return { ok: true };
  } catch (error: unknown) {
    const message = extractErrorMessage(error);
    logger.error('[Notifications Store] Failed to send test notification:', message ?? error);
    return { ok: false, error: message };
  }
}

export async function testGenericWebhook(
  url?: string,
  headers?: string
): Promise<{ ok: boolean; error?: string }> {
  if (!browser) return { ok: false };
  try {
    await notificationsApi.testWebhook(url, headers);
    return { ok: true };
  } catch (error: unknown) {
    const message = extractErrorMessage(error);
    logger.error('[Notifications Store] Failed to send webhook test notification:', message ?? error);
    return { ok: false, error: message };
  }
}
