import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import ar from './ar.json';
import en from './en.json';

export const languages = ['en', 'ar'] as const;
export type Language = (typeof languages)[number];

const LANGUAGE_KEY = 'fms.lang';

export const directionOf = (lng: string): 'rtl' | 'ltr' => (lng.startsWith('ar') ? 'rtl' : 'ltr');

/** Keeps <html lang> and <html dir> in step with the active language. */
export function applyDocumentLanguage(lng: string, root: HTMLElement = document.documentElement) {
  root.lang = lng;
  root.dir = directionOf(lng);
}

/** The saved choice if there is one, otherwise the first browser language we support, otherwise English. */
export function initialLanguage(saved: string | null, browserLanguages: readonly string[]): Language {
  if (saved && (languages as readonly string[]).includes(saved)) return saved as Language;
  for (const candidate of browserLanguages) {
    const base = candidate.toLowerCase().split('-')[0];
    if ((languages as readonly string[]).includes(base)) return base as Language;
  }

  return 'en';
}

function readSaved(): string | null {
  try {
    return localStorage.getItem(LANGUAGE_KEY);
  } catch {
    return null;
  }
}

const startLanguage = initialLanguage(readSaved(), typeof navigator === 'undefined' ? [] : (navigator.languages ?? [navigator.language]));

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, ar: { translation: ar } },
  lng: startLanguage,
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
});

// Applied now (the first language never "changes") and on every change.
applyDocumentLanguage(startLanguage);
i18n.on('languageChanged', (lng) => {
  applyDocumentLanguage(lng);
  try {
    localStorage.setItem(LANGUAGE_KEY, lng);
  } catch {
    // Storage unavailable: the choice lasts for this visit only.
  }
});

export default i18n;
