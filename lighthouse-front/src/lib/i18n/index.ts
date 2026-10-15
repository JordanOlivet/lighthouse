import i18n from 'i18next';
import { writable, derived } from 'svelte/store';
import en from './en';
import fr from './fr';
import es from './es';
import { browser } from '$app/env';

export type Locale = 'en' | 'fr' | 'es';

export interface AvailableLocale {
  code: Locale;
  name: string;
  flag: string;
}

export const availableLocales: AvailableLocale[] = [
  { code: 'en', name: 'English', flag: '🇬🇧' },
  { code: 'fr', name: 'Français', flag: '🇫🇷' },
  { code: 'es', name: 'Español', flag: '🇪🇸' }
];

const savedLocale = browser ? (localStorage.getItem('locale') as Locale) || 'en' : 'en';

i18n.init({
  resources: {
    en: { translation: en },
    fr: { translation: fr },
    es: { translation: es }
  },
  lng: savedLocale,
  fallbackLng: 'en',
  interpolation: {
    escapeValue: false, // Svelte handles escaping
    prefix: '{',
    suffix: '}'
  }
});

// Use writable store for locale to maintain compatibility with $locale syntax
export const locale = writable<Locale>(i18n.language as Locale);

// Version counter to trigger reactivity on language change
const translationVersion = writable<number>(0);

// Derived translation function - works with $ syntax
export const t = derived(
  translationVersion,
  () => {
    return (key: string, options?: Record<string, unknown>): string => {
      return i18n.t(key, options);
    };
  }
);

// Helper to get current translation function (for use in non-reactive contexts)
export function getT(): (key: string, options?: Record<string, unknown>) => string {
  return (key: string, options?: Record<string, unknown>) => i18n.t(key, options);
}

// Set locale function
export function setLocale(lng: Locale) {
  locale.set(lng);
  if (browser) {
    localStorage.setItem('locale', lng);
  }
  i18n.changeLanguage(lng);
  translationVersion.update(v => v + 1);
}

// Get current locale
export function getLocale(): Locale {
  let currentLocale: Locale = 'en';
  locale.subscribe(l => currentLocale = l)();
  return currentLocale;
}

// Listen to language changes from i18next (e.g., from i18n.changeLanguage)
i18n.on('languageChanged', (lng) => {
  locale.set(lng as Locale);
});

export default i18n;
