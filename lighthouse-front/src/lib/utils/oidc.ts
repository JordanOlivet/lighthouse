import type { OidcConfig } from '$lib/types';

const DEFAULT_RETURN_URL = '/dashboard';

/** Query parameters on /login that must stop the automatic redirect to the SSO provider. */
const AUTO_REDIRECT_BYPASS_PARAMS = ['local', 'logged_out', 'oidc_error'];

/**
 * Whether /login should send the browser straight to the SSO provider.
 * Bypassed after a logout, after an SSO error (avoids a redirect loop) and with ?local=1.
 */
export function shouldAutoRedirect(config: OidcConfig, params: Pick<URLSearchParams, 'has'>): boolean {
  if (!config.enabled || !config.autoRedirect) return false;
  return !AUTO_REDIRECT_BYPASS_PARAMS.some((p) => params.has(p));
}

/**
 * Keeps only same-origin paths (open-redirect guard, mirrors the backend ReturnUrlHelper).
 * Login pages are not valid targets: they would loop back into the login flow.
 */
export function safeReturnUrl(raw: string | null | undefined): string {
  if (!raw || !raw.startsWith('/')) return DEFAULT_RETURN_URL;
  if (raw.length > 1 && (raw[1] === '/' || raw[1] === '\\')) return DEFAULT_RETURN_URL;
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(raw)) return DEFAULT_RETURN_URL;
  if (raw === '/login' || raw.startsWith('/login?') || raw.startsWith('/login/')) return DEFAULT_RETURN_URL;
  return raw;
}

/**
 * Relative on purpose: the OIDC callback and the refresh cookie must stay on the
 * frontend origin (Nginx in production, the Vite /api proxy in dev), never VITE_API_URL.
 */
export function oidcLoginUrl(returnUrl: string): string {
  return `/api/auth/oidc/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}

/** Starts an explicit link of the signed-in account (ticket from POST /api/auth/oidc/link). */
export function oidcLinkUrl(ticket: string, returnUrl: string): string {
  return `${oidcLoginUrl(returnUrl)}&linkTicket=${encodeURIComponent(ticket)}`;
}

/** Error codes the backend can append as ?oidc_link_error= after an explicit link. */
export const OIDC_LINK_ERROR_CODES = [
  'identity_in_use',
  'subject_mismatch',
  'link_invalid',
  'account_disabled',
  'account_not_found',
  'provider_error'
] as const;

export interface LinkOutcome {
  linked: boolean;
  error: string | null;
}

/** Reads ?oidc_linked=1 / ?oidc_link_error=<code> set by the backend at the end of a link. */
export function readLinkOutcome(params: Pick<URLSearchParams, 'get'>): LinkOutcome | null {
  if (params.get('oidc_linked') === '1') return { linked: true, error: null };
  const code = params.get('oidc_link_error');
  if (code === null) return null;
  const known = (OIDC_LINK_ERROR_CODES as readonly string[]).includes(code);
  return { linked: false, error: known ? code : 'provider_error' };
}
