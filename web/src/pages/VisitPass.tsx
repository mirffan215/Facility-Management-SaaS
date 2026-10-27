import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { languages } from '../i18n';
import { formatDate } from '../lib/format';
import type { VisitPass } from '../api/types';

/** What a visitor sees when they open their pass link. No sign-in: the code in the address is the credential. Nothing here uses the signed-in app. */
export function VisitPassPage() {
  const { t, i18n } = useTranslation();
  const code = window.location.pathname.split('/').filter(Boolean)[1] ?? '';
  const [pass, setPass] = useState<VisitPass | null | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    fetch(`/api/v1/visits/pass/${encodeURIComponent(code)}`)
      .then(async (r) => (r.ok ? ((await r.json()) as VisitPass) : null))
      .then((p) => !cancelled && setPass(p))
      .catch(() => !cancelled && setPass(null));
    return () => {
      cancelled = true;
    };
  }, [code]);

  return (
    <main className="signin">
      <h1>{t('visitPass.title')}</h1>
      <label>
        <span className="visually-hidden">{t('language.label')}</span>
        <select value={i18n.resolvedLanguage} onChange={(e) => void i18n.changeLanguage(e.target.value)}>
          {languages.map((l) => <option key={l} value={l}>{t(`language.${l}`)}</option>)}
        </select>
      </label>
      {pass === undefined ? <p role="status">{t('common.loading')}</p> : null}
      {pass === null ? <p className="banner error" role="alert">{t('visitPass.invalid')}</p> : null}
      {pass ? (
        <>
          <p className="muted">{t('visitPass.show')}</p>
          <img src={`/api/v1/visits/pass/${encodeURIComponent(code)}/qr.svg`} alt={t('visitPass.qrAlt')} width={240} height={240} />
          <dl className="details">
            <dt>{t('visitors.visitor')}</dt><dd><bdi>{pass.visitorName}</bdi></dd>
            <dt>{t('visitors.site')}</dt><dd><bdi>{pass.siteName}</bdi></dd>
            <dt>{t('visitors.host')}</dt><dd><bdi>{pass.hostName}</bdi></dd>
            <dt>{t('visitors.expected')}</dt><dd>{formatDate(pass.expectedAt, i18n.resolvedLanguage ?? 'en', 'gregorian', true)}</dd>
          </dl>
        </>
      ) : null}
    </main>
  );
}
