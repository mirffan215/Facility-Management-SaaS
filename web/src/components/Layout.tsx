import { NavLink, Outlet } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { useMe, usePermissions, useUnreadCount } from '../api/hooks';
import { languages } from '../i18n';
import { usePrefs } from '../lib/prefs';
import { SyncStatus } from '../offline/SyncStatus';
import { useOffline } from '../offline/OfflineProvider';

export function Layout() {
  const { t, i18n } = useTranslation();
  const { displayName, signOut, offlineSession } = useAuth();
  const offline = useOffline();
  const { calendar, setCalendar } = usePrefs();
  const { can } = usePermissions();
  const me = useMe();
  const unread = useUnreadCount().data?.count ?? 0;

  return (
    <>
      <a className="skip-link" href="#main">
        {t('common.skipToContent')}
      </a>
      <header className="topbar">
        <strong className="brand">{t('app.title')}</strong>
        <nav aria-label={t('nav.main')}>
          {can('workorders.work') || offlineSession || !!offline.snapshot ? <NavLink to="/jobs">{t('nav.jobs')}</NavLink> : null}
          {can('locations.read') ? <NavLink to="/locations">{t('nav.locations')}</NavLink> : null}
          {can('assets.read') ? <NavLink to="/assets">{t('nav.assets')}</NavLink> : null}
          {can('workorders.read') || can('workorders.request') ? <NavLink to="/work-orders">{t('nav.workOrders')}</NavLink> : null}
          {can('inventory.read') ? <NavLink to="/inventory">{t('nav.inventory')}</NavLink> : null}
          {can('purchasing.read') || can('purchasing.request') ? <NavLink to="/purchasing">{t('nav.purchasing')}</NavLink> : null}
          {can('integration.read') ? <NavLink to="/integrations">{t('nav.integrations')}</NavLink> : null}
          {can('vendors.read') ? <NavLink to="/vendors">{t('nav.vendors')}</NavLink> : null}
          {can('pm.read') ? <NavLink to="/pm">{t('nav.pm')}</NavLink> : null}
          {can('locations.manage') || can('assets.manage') ? <NavLink to="/import">{t('nav.import')}</NavLink> : null}
          {can('assets.manage') || can('customfields.manage') || can('workorders.manage') ? <NavLink to="/setup">{t('nav.setup')}</NavLink> : null}
          {can('users.read') ? <NavLink to="/users">{t('nav.users')}</NavLink> : null}
          {!offlineSession ? <NavLink to="/notifications">
            {t('notify.title')}
            {unread > 0 ? (
              <>
                {' '}
                <span className="count">{unread > 99 ? '99+' : unread}</span>
                <span className="visually-hidden"> {t('notify.unreadSuffix')}</span>
              </>
            ) : null}
          </NavLink> : null}
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
          <button type="button" onClick={async () => {
            // Signing out removes the saved jobs from this phone. Work that has not been sent would be lost: ask first.
            if (offline.state.pending + offline.state.failed > 0 && !window.confirm(t('offline.signOutWarning', { count: offline.state.pending + offline.state.failed }))) return;
            await offline.forgetEverything();
            await signOut();
          }}>
            {t('auth.signOut')}
          </button>
        </div>
      </header>
      <SyncStatus />
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
