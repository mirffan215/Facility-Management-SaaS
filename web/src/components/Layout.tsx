import { NavLink, Outlet } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { useMe, usePermissions } from '../api/hooks';
import { languages } from '../i18n';
import { usePrefs } from '../lib/prefs';

export function Layout() {
  const { t, i18n } = useTranslation();
  const { displayName, signOut } = useAuth();
  const { calendar, setCalendar } = usePrefs();
  const { can } = usePermissions();
  const me = useMe();

  return (
    <>
      <a className="skip-link" href="#main">
        {t('common.skipToContent')}
      </a>
      <header className="topbar">
        <strong className="brand">{t('app.title')}</strong>
        <nav aria-label={t('nav.main')}>
          {can('locations.read') ? <NavLink to="/locations">{t('nav.locations')}</NavLink> : null}
          {can('assets.read') ? <NavLink to="/assets">{t('nav.assets')}</NavLink> : null}
          {can('workorders.read') || can('workorders.request') ? <NavLink to="/work-orders">{t('nav.workOrders')}</NavLink> : null}
          {can('locations.manage') || can('assets.manage') ? <NavLink to="/import">{t('nav.import')}</NavLink> : null}
          {can('assets.manage') || can('customfields.manage') || can('workorders.manage') ? <NavLink to="/setup">{t('nav.setup')}</NavLink> : null}
          {can('users.read') ? <NavLink to="/users">{t('nav.users')}</NavLink> : null}
        </nav>
        <div className="prefs">
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
          <label>
            <span className="visually-hidden">{t('calendar.label')}</span>
            <select value={calendar} onChange={(e) => setCalendar(e.target.value as 'gregorian' | 'hijri')}>
              <option value="gregorian">{t('calendar.gregorian')}</option>
              <option value="hijri">{t('calendar.hijri')}</option>
            </select>
          </label>
          {displayName ? <span className="who">{displayName}</span> : null}
          <button type="button" onClick={() => void signOut()}>
            {t('auth.signOut')}
          </button>
        </div>
      </header>
      <main id="main" tabIndex={-1}>
        {me.error instanceof ApiError && me.error.isForbidden ? (
          <section>
            <h1>{t('users.deactivatedAccount')}</h1>
            <p>{t('users.deactivatedHelp')}</p>
          </section>
        ) : (
          <Outlet />
        )}
      </main>
    </>
  );
}
