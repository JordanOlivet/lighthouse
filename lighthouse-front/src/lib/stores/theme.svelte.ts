import { browser } from '$app/env';

export type Theme = 'light' | 'dark';

function getInitialTheme(): Theme {
  if (!browser) return 'light';

  // Check localStorage first
  const savedTheme = localStorage.getItem('theme') as Theme | null;
  if (savedTheme) {
    return savedTheme;
  }
  // Check system preference
  if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
    return 'dark';
  }
  return 'light';
}

function applyTheme(newTheme: Theme) {
  if (!browser) return;

  const root = document.documentElement;
  if (newTheme === 'dark') {
    root.classList.add('dark');
  } else {
    root.classList.remove('dark');
  }
  localStorage.setItem('theme', newTheme);
}

// Svelte 5 pattern: export state object
export const themeState = $state({
  current: getInitialTheme()
});

// Derived state as getter - Svelte 5 doesn't allow exporting $derived
export const isDark = {
  get current() { return themeState.current === 'dark'; }
};

// Apply initial theme
if (browser) {
  applyTheme(themeState.current);
}

// Actions
export function toggle() {
  themeState.current = themeState.current === 'light' ? 'dark' : 'light';
  applyTheme(themeState.current);
}

export function setTheme(newTheme: Theme) {
  themeState.current = newTheme;
  applyTheme(newTheme);
}
