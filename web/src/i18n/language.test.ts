import { describe, expect, it } from 'vitest';
import i18n, { applyDocumentLanguage, directionOf, initialLanguage } from './index';

describe('initialLanguage', () => {
  it('uses a saved choice first', () => {
    expect(initialLanguage('ar', ['en-GB'])).toBe('ar');
    expect(initialLanguage('en', ['ar-SA'])).toBe('en');
  });

  it('ignores a saved value it does not support', () => {
    expect(initialLanguage('fr', ['ar'])).toBe('ar');
  });

  it('follows the browser: the first supported language wins', () => {
    expect(initialLanguage(null, ['ar-SA', 'en'])).toBe('ar');
    expect(initialLanguage(null, ['fr-FR', 'ar'])).toBe('ar');
    expect(initialLanguage(null, ['EN-us'])).toBe('en');
  });

  it('falls back to English', () => {
    expect(initialLanguage(null, ['fr', 'de'])).toBe('en');
    expect(initialLanguage(null, [])).toBe('en');
  });
});

describe('document direction', () => {
  it('is right-to-left for Arabic only', () => {
    expect(directionOf('ar')).toBe('rtl');
    expect(directionOf('ar-SA')).toBe('rtl');
    expect(directionOf('en')).toBe('ltr');
  });

  it('is applied to the page element', () => {
    const root = document.createElement('html');
    applyDocumentLanguage('ar', root);
    expect([root.lang, root.dir]).toEqual(['ar', 'rtl']);
    applyDocumentLanguage('en', root);
    expect([root.lang, root.dir]).toEqual(['en', 'ltr']);
  });

  it('is updated and remembered when the language changes', async () => {
    await i18n.changeLanguage('ar');
    expect(document.documentElement.dir).toBe('rtl');
    expect(localStorage.getItem('fms.lang')).toBe('ar');
    await i18n.changeLanguage('en');
    expect(document.documentElement.dir).toBe('ltr');
    expect(localStorage.getItem('fms.lang')).toBe('en');
  });
});
