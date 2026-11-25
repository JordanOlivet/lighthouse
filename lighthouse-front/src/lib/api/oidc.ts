import { apiClient } from './client';
import type { ApiResponseWrapper, OidcConfig } from '$lib/types';

const DISABLED: OidcConfig = { enabled: false, displayName: 'SSO', autoRedirect: false };

/** SSO settings for the login page. Falls back to "disabled" if the backend can't be reached. */
export const getOidcConfig = async (): Promise<OidcConfig> => {
  try {
    const response = await apiClient.get<ApiResponseWrapper<OidcConfig>>('/auth/oidc/config');
    return response.data.data ?? DISABLED;
  } catch {
    return DISABLED;
  }
};

/** Single-use ticket that lets the browser navigation to the provider link the signed-in account. */
export const startLink = async (): Promise<string> => {
  const response = await apiClient.post<ApiResponseWrapper<{ ticket: string }>>('/auth/oidc/link');
  return response.data.data!.ticket;
};

export const oidcApi = { getOidcConfig, startLink };
