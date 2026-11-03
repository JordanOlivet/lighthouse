import { apiClient } from './client';

export interface TestNotificationRequest {
  webhookUrl?: string;
}

export interface TestWebhookRequest {
  url?: string;
  headers?: string;
}

const notificationsApi = {
  /**
   * Send a test Discord notification. If a webhook URL is provided it is tested
   * (lets the user verify before saving), otherwise the saved one is used.
   */
  testDiscord: async (webhookUrl?: string): Promise<void> => {
    await apiClient.post('/notifications/test', { webhookUrl } as TestNotificationRequest);
  },

  /**
   * Send a test notification to the generic webhook. `url` / `headers` are only
   * sent when the user edited them; omitted values fall back to the saved ones.
   */
  testWebhook: async (url?: string, headers?: string): Promise<void> => {
    await apiClient.post('/notifications/test/webhook', { url, headers } as TestWebhookRequest);
  },
};

export default notificationsApi;
