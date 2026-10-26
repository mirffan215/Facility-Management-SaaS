import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { languages } from '../i18n';

export function SignIn() {
  const { t, i18n } = useTranslation();
  const { status, providers, reason, signIn } = useAuth();
  const usable = providers.filter((p) => p.clientId);

  return (
    <main className="signin">
      <h1>{t('app.title')}</h1>
      <label>
        <span className="visually-hidden">{t('language.label')}</span>
        <select value={i18n.resolvedLanguage} onChange={(e) => void i18n.changeLanguage(e.target.value)}>
          {languages.map((l) => (
            <option key={l} value={l}>
              {t(`language.${l}`)}
            </option>
          ))}
        </select>
      </label>

      {status === 'unreachable' ? (
        <p className="banner error" role="alert">
          {t('auth.unreachable')}
        </p>
      ) : (
        <>
          {reason === 'expired' ? (
            <p className="banner" role="status">
              {t('auth.expired')}
            </p>
          ) : null}
          {status === 'failed' ? (
            <p className="banner error" role="alert">
              {t('auth.failed')}
            </p>
          ) : null}
          {usable.length === 0 ? (
            <p className="banner error" role="alert">
              {t('auth.noProviders')}
            </p>
          ) : (
            <>
              <p>{t('auth.prompt')}</p>
              <div className="stack">
                {usable.map((p) => (
                  <button key={p.name} type="button" className="primary" onClick={() => void signIn(p.name)}>
                    {t('auth.signInWith', { provider: p.name })}
                  </button>
                ))}
              </div>
            </>
          )}
        </>
      )}
    </main>
  );
}
