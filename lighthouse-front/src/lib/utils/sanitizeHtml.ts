import { browser } from '$app/env';

// Minimal, dependency-free HTML sanitizer for rendering trusted-ish markdown output
// (e.g. GitHub release notes) via {@html}. It parses the HTML, then walks the tree and
// drops anything not on the allowlist: disallowed elements are removed entirely,
// disallowed attributes are stripped, and URL attributes are restricted to safe
// schemes. This is intentionally conservative — it is not a general-purpose sanitizer,
// only enough to neutralize scripts, event handlers and javascript:/data: URLs in
// changelog content.

const ALLOWED_TAGS = new Set([
  'a', 'p', 'br', 'hr', 'span', 'div',
  'strong', 'b', 'em', 'i', 'u', 'del', 's', 'small', 'sub', 'sup',
  'code', 'pre', 'kbd', 'samp',
  'ul', 'ol', 'li',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'blockquote',
  'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td',
  'img'
]);

// Attributes allowed per tag (plus the global set below).
const ALLOWED_ATTRS: Record<string, Set<string>> = {
  a: new Set(['href', 'title', 'target', 'rel']),
  img: new Set(['src', 'alt', 'title', 'width', 'height'])
};
const GLOBAL_ALLOWED_ATTRS = new Set(['title']);

// URL attributes whose value must use a safe scheme.
const URL_ATTRS = new Set(['href', 'src']);
const SAFE_URL = /^(https?:|mailto:|#|\/)/i;

function isSafeUrl(value: string): boolean {
  // Strip control chars and spaces (code point <= 0x20) before testing the scheme:
  // browsers ignore embedded control chars, so "java\tscript:alert(1)" resolves to
  // javascript:. Filtering by code point avoids putting control chars in this source.
  const cleaned = Array.from(value)
    .filter((ch) => ch.charCodeAt(0) > 0x20)
    .join('');
  if (cleaned === '') return false;
  return SAFE_URL.test(cleaned);
}

function sanitizeElement(el: Element): void {
  const tag = el.tagName.toLowerCase();

  // Remove disallowed elements entirely (including their subtree). script/style/iframe
  // etc. never reach here on the allowlist.
  if (!ALLOWED_TAGS.has(tag)) {
    el.remove();
    return;
  }

  // Strip disallowed / dangerous attributes.
  for (const attr of Array.from(el.attributes)) {
    const name = attr.name.toLowerCase();
    const perTag = ALLOWED_ATTRS[tag];
    const allowed = GLOBAL_ALLOWED_ATTRS.has(name) || (perTag?.has(name) ?? false);

    if (!allowed || name.startsWith('on')) {
      el.removeAttribute(attr.name);
      continue;
    }

    if (URL_ATTRS.has(name) && !isSafeUrl(attr.value)) {
      el.removeAttribute(attr.name);
    }
  }

  // Harden external links.
  if (tag === 'a' && el.getAttribute('target') === '_blank') {
    el.setAttribute('rel', 'noopener noreferrer');
  }

  // Recurse (snapshot children first — the list mutates as we remove nodes).
  for (const child of Array.from(el.children)) {
    sanitizeElement(child);
  }
}

/**
 * Sanitize an HTML string, returning markup safe to inject with {@html}.
 * Returns an empty string outside the browser (no DOMParser available).
 */
export function sanitizeHtml(html: string): string {
  if (!browser) return '';

  const doc = new DOMParser().parseFromString(html, 'text/html');
  for (const child of Array.from(doc.body.children)) {
    sanitizeElement(child);
  }
  return doc.body.innerHTML;
}
