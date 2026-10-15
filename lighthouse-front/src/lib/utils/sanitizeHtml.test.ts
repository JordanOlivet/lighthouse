import { describe, it, expect, vi } from 'vitest';

// Force the browser code path so the DOMParser-based sanitizer runs (jsdom provides it).
vi.mock('$app/env', () => ({ browser: true }));

import { sanitizeHtml } from './sanitizeHtml';

describe('sanitizeHtml', () => {
  it('keeps allowed formatting tags and text', () => {
    const out = sanitizeHtml('<p>Hello <strong>world</strong> <em>!</em></p>');
    expect(out).toBe('<p>Hello <strong>world</strong> <em>!</em></p>');
  });

  it('removes script elements entirely', () => {
    const out = sanitizeHtml('<p>ok</p><script>alert(1)</script>');
    expect(out).toContain('<p>ok</p>');
    expect(out.toLowerCase()).not.toContain('<script');
    expect(out).not.toContain('alert(1)');
  });

  it('strips inline event handlers', () => {
    const out = sanitizeHtml('<a href="https://ex.com" onclick="steal()">x</a>');
    expect(out).not.toContain('onclick');
    expect(out).not.toContain('steal');
    expect(out).toContain('href="https://ex.com"');
  });

  it('drops javascript: URLs on href', () => {
    const out = sanitizeHtml('<a href="javascript:alert(1)">x</a>');
    expect(out).not.toContain('javascript:');
    expect(out).toContain('>x</a>');
  });

  it('drops javascript: URLs smuggled with control chars', () => {
    const out = sanitizeHtml('<a href="java\tscript:alert(1)">x</a>');
    expect(out.toLowerCase()).not.toContain('script:');
  });

  it('drops data: URLs on img src', () => {
    const out = sanitizeHtml('<img src="data:text/html;base64,PHN2Zz4=" alt="x">');
    expect(out).not.toContain('data:');
  });

  it('keeps safe http/mailto/relative/anchor URLs', () => {
    expect(sanitizeHtml('<a href="https://a.com">a</a>')).toContain('href="https://a.com"');
    expect(sanitizeHtml('<a href="mailto:x@y.com">a</a>')).toContain('href="mailto:x@y.com"');
    expect(sanitizeHtml('<a href="/rel">a</a>')).toContain('href="/rel"');
    expect(sanitizeHtml('<a href="#frag">a</a>')).toContain('href="#frag"');
  });

  it('removes iframe/object/style elements', () => {
    const out = sanitizeHtml('<iframe src="https://evil"></iframe><style>*{}</style><b>keep</b>');
    expect(out.toLowerCase()).not.toContain('<iframe');
    expect(out.toLowerCase()).not.toContain('<style');
    expect(out).toContain('<b>keep</b>');
  });

  it('adds rel=noopener to target=_blank links', () => {
    const out = sanitizeHtml('<a href="https://a.com" target="_blank">a</a>');
    expect(out).toContain('rel="noopener noreferrer"');
  });
});
