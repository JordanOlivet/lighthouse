import { describe, it, expect } from 'vitest';
import { shouldAutoRedirect, safeReturnUrl, oidcLoginUrl, oidcLinkUrl, readLinkOutcome, ssoMenuState } from './oidc';

const on = { enabled: true, displayName: 'Pocket ID', autoRedirect: true };

describe('shouldAutoRedirect', () => {
  it('auto-redirects when enabled and no bypass param', () => {
    expect(shouldAutoRedirect(on, new URLSearchParams())).toBe(true);
  });

  it('keeps auto-redirecting with only a returnUrl', () => {
    expect(shouldAutoRedirect(on, new URLSearchParams('returnUrl=%2Fcompose'))).toBe(true);
  });

  it.each(['local=1', 'logged_out=1', 'oidc_error=account_not_found', 'reset=success'])(
    'does not auto-redirect with %s',
    (query) => {
      expect(shouldAutoRedirect(on, new URLSearchParams(query))).toBe(false);
    }
  );

  it('never auto-redirects when disabled or autoRedirect off', () => {
    expect(shouldAutoRedirect({ ...on, enabled: false }, new URLSearchParams())).toBe(false);
    expect(shouldAutoRedirect({ ...on, autoRedirect: false }, new URLSearchParams())).toBe(false);
  });
});

describe('safeReturnUrl', () => {
  it.each([
    [null, '/dashboard'],
    ['', '/dashboard'],
    ['/compose?x=1', '/compose?x=1'],
    ['//evil.com', '/dashboard'],
    ['https://evil.com', '/dashboard'],
    ['/\\evil.com', '/dashboard'],
    ['dashboard', '/dashboard'],
    ['/login', '/dashboard'],
    ['/login?local=1', '/dashboard']
  ])('safeReturnUrl(%s) → %s', (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });
});

describe('oidcLoginUrl', () => {
  it('is a relative URL with an encoded returnUrl', () => {
    expect(oidcLoginUrl('/compose?x=1')).toBe('/api/auth/oidc/login?returnUrl=%2Fcompose%3Fx%3D1');
  });
});

describe('oidcLinkUrl', () => {
  it('carries the ticket and the page to come back to', () => {
    expect(oidcLinkUrl('t-1_x', '/users?tab=2')).toBe(
      '/api/auth/oidc/login?returnUrl=%2Fusers%3Ftab%3D2&linkTicket=t-1_x'
    );
  });
});

describe('ssoMenuState', () => {
  const enabled = { enabled: true };

  it('offers the link action to an unlinked account', () => {
    expect(ssoMenuState(enabled, { oidcLinked: false, hasPassword: true })).toBe('link');
  });

  it('shows the link and allows unlinking when the account has a password', () => {
    expect(ssoMenuState(enabled, { oidcLinked: true, hasPassword: true })).toBe('linked');
  });

  it('shows the link without an unlink action for an SSO-only account', () => {
    expect(ssoMenuState(enabled, { oidcLinked: true, hasPassword: false })).toBe('linked-sso-only');
  });

  it('shows nothing when SSO is off, whatever the account state', () => {
    expect(ssoMenuState({ enabled: false }, { oidcLinked: true, hasPassword: true })).toBe('hidden');
    expect(ssoMenuState({ enabled: false }, { oidcLinked: false, hasPassword: true })).toBe('hidden');
  });

  it('shows nothing until the config and the user are loaded', () => {
    expect(ssoMenuState(null, { oidcLinked: true, hasPassword: true })).toBe('hidden');
    expect(ssoMenuState(enabled, null)).toBe('hidden');
  });

  it('shows nothing when the link state is unknown (older API response)', () => {
    const user = { hasPassword: true } as { oidcLinked: boolean; hasPassword: boolean };
    expect(ssoMenuState(enabled, user)).toBe('hidden');
  });
});

describe('readLinkOutcome', () => {
  it('returns null without link params', () => {
    expect(readLinkOutcome(new URLSearchParams('tab=2'))).toBeNull();
  });

  it('reports a successful link', () => {
    expect(readLinkOutcome(new URLSearchParams('oidc_linked=1'))).toEqual({ linked: true, error: null });
  });

  it.each(['identity_in_use', 'subject_mismatch', 'link_invalid', 'provider_error', 'account_disabled'])(
    'reports the known error %s',
    (code) => {
      expect(readLinkOutcome(new URLSearchParams(`oidc_link_error=${code}`))).toEqual({ linked: false, error: code });
    }
  );

  it('maps an unknown error code to provider_error', () => {
    expect(readLinkOutcome(new URLSearchParams('oidc_link_error=<script>'))).toEqual({
      linked: false,
      error: 'provider_error'
    });
  });
});
