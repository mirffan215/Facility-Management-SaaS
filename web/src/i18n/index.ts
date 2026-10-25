import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import ar from './ar.json';
import en from './en.json';

export const languages = ['en', 'ar'] as const;
export type Language = (typeof languages)[number];

export const directionOf = (lng: string): 'rtl' | 'ltr' => (lng.startsWith('ar') ? 'rtl' : 'ltr');

/** Keeps <html lang> and <html dir> in step with the active language. */
export function applyDocumentLanguage(lng: string, root: HTMLElement = document.documentElement) {
  root.lang = lng;
  root.dir = directionOf(lng);
}

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, ar: { translation: ar } },
  lng: 'en',
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
});

i18n.on('languageChanged', (lng) => applyDocumentLanguage(lng));

export default i18n;
