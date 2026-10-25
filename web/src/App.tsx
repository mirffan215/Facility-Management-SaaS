import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { languages } from './i18n';

interface SystemInfo {
  deploymentMode: string;
  edition: string;
  licensed: boolean;
}

export function App() {
  const { t, i18n } = useTranslation();
  const [info, setInfo] = useState<SystemInfo | 'error' | null>(null);

  useEffect(() => {
    fetch('/api/v1/system/info')
      .then((r) => (r.ok ? (r.json() as Promise<SystemInfo>) : Promise.reject(new Error(String(r.status)))))
      .then(setInfo)
      .catch(() => setInfo('error'));
  }, []);

  return (
    <main>
      <header>
        <h1>{t('app.title')}</h1>
        <label>
          {t('language.label')}{' '}
          <select value={i18n.language} onChange={(e) => void i18n.changeLanguage(e.target.value)}>
            {languages.map((l) => (
              <option key={l} value={l}>
                {t(`language.${l}`)}
              </option>
            ))}
          </select>
        </label>
      </header>
      <section>
        <h2>{t('system.heading')}</h2>
        {info === null && <p>{t('system.loading')}</p>}
        {info === 'error' && <p role="alert">{t('system.error')}</p>}
        {info && info !== 'error' && (
          <dl>
            <dt>{t('system.mode')}</dt>
            <dd>{info.deploymentMode}</dd>
            <dt>{t('system.edition')}</dt>
            <dd>{info.edition}</dd>
            <dt>{t('system.licensed')}</dt>
            <dd>{info.licensed ? t('system.yes') : t('system.no')}</dd>
          </dl>
        )}
      </section>
    </main>
  );
}
