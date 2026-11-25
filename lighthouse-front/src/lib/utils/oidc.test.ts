import { describe, it, expect } from 'vitest';
import { shouldAutoRedirect, safeReturnUrl, oidcLoginUrl } from './oidc';

const on = { enabled: true, displayName: 'Pocket ID', autoRedirect: true };

describe('shouldAutoRedirect', () => {
  it('auto-redirects when enabled and no bypass param', () => {
    expect(shouldAutoRedirect(on, new URLSearchParams())).toBe(true);
  });

  it('keeps auto-redirecting with only a returnUrl', () => {
    expect(shouldAutoRedirect(on, new URLSearchParams('returnUrl=%2Fcompose'))).toBe(true);
  });

  it.each(['local=1', 'logged_out=1', 'oidc_error=account_not_found'])(
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
