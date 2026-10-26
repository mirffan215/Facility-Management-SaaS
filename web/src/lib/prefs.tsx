import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatDate, formatNumber, localizedName, type Calendar } from './format';

interface Prefs {
  lang: string;
  calendar: Calendar;
  setCalendar: (calendar: Calendar) => void;
  date: (value: string | null | undefined, withTime?: boolean) => string;
  num: (value: number | null | undefined) => string;
  name: (entity: { name: string; nameAr: string | null }) => string;
}

const PrefsContext = createContext<Prefs | null>(null);
const CALENDAR_KEY = 'fms.calendar';

function readCalendar(): Calendar {
  try {
    return localStorage.getItem(CALENDAR_KEY) === 'hijri' ? 'hijri' : 'gregorian';
  } catch {
    return 'gregorian';
  }
}

export function PrefsProvider({ children }: { children: ReactNode }) {
  const { i18n } = useTranslation();
  const [calendar, setCalendarState] = useState<Calendar>(readCalendar);
  const lang = i18n.resolvedLanguage ?? i18n.language ?? 'en';

  useEffect(() => {
    try {
      localStorage.setItem(CALENDAR_KEY, calendar);
    } catch {
      // Storage unavailable (private mode): the choice just lasts for this visit.
    }
  }, [calendar]);

  const value = useMemo<Prefs>(
    () => ({
      lang,
      calendar,
      setCalendar: setCalendarState,
      date: (v, withTime) => formatDate(v, lang, calendar, withTime),
      num: (v) => formatNumber(v, lang),
      name: (e) => localizedName(e, lang),
    }),
    [lang, calendar],
  );
  return <PrefsContext.Provider value={value}>{children}</PrefsContext.Provider>;
}

export function usePrefs(): Prefs {
  const value = useContext(PrefsContext);
  if (!value) throw new Error('usePrefs must be used inside <PrefsProvider>.');
  return value;
}
