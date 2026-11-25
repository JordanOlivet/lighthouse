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

export const oidcApi = { getOidcConfig };
