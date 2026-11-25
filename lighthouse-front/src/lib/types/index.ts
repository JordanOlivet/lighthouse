// Re-export all types from individual modules
export * from './compose';
export * from './container';
export * from './image';
export * from './operations';
export * from './permissions';
export * from './global';
export * from './api';
export * from './registry';
export * from './table';
export * from './logs';

// Base types
export interface User {
  id: number;
  username: string;
  email?: string;
  role: string;
  isEnabled: boolean;
  mustChangePassword: boolean;
  mustAddEmail: boolean;
  createdAt: string;
  lastLoginAt?: string;
  /** Linked to an SSO (OIDC) identity. */
  oidcLinked: boolean;
  /** False for OIDC-only accounts (no local password). */
  hasPassword: boolean;
}

/** SSO login settings exposed by GET /api/auth/oidc/config. */
export interface OidcConfig {
  enabled: boolean;
  displayName: string;
  autoRedirect: boolean;
}

export interface LoginRequest {
  username: string;
  password: string;
  rememberMe?: boolean;
}

export interface LoginResponse {
  accessToken: string;
  /** Deprecated: the refresh token now lives in an HttpOnly cookie and is returned empty. */
  refreshToken?: string;
  username: string;
  role: string;
  mustChangePassword: boolean;
  mustAddEmail: boolean;
}

export interface ApiResponseWrapper<T> {
  data?: T;
  success: boolean;
  message?: string;
  errors?: Record<string, string[]>;
  errorCode?: string;
}
