import { apiClient } from './client';
import type { ApiResponseWrapper, OidcConfig, User } from '$lib/types';

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

export interface OidcUnlinkResult {
  /** Replaces the current access token (the old one is revoked); null when nothing was linked. */
  accessToken: string | null;
  user: User;
}

/**
 * Removes the signed-in account's own SSO link. The backend ends the account's other sessions
 * and re-issues this one (new access token here, refresh token via the HttpOnly cookie).
 */
export const unlinkSelf = async (): Promise<OidcUnlinkResult> => {
  const response = await apiClient.delete<ApiResponseWrapper<OidcUnlinkResult>>('/auth/oidc/link');
  return response.data.data!;
};

export const oidcApi = { getOidcConfig, startLink, unlinkSelf };
