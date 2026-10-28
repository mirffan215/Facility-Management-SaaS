import { NavLink, Outlet } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { useMe, usePermissions, usePortalOnly, useSystemInfo, useUnreadCount } from '../api/hooks';
import { languages } from '../i18n';
import { usePrefs } from '../lib/prefs';
import { SyncStatus } from '../offline/SyncStatus';
import { useOffline } from '../offline/OfflineProvider';

export function Layout() {
  const { t, i18n } = useTranslation();
  const { displayName, signOut, offlineSession } = useAuth();
  const offline = useOffline();
  const { calendar, setCalendar, date } = usePrefs();
  const { can } = usePermissions();
  const me = useMe();
  const portalOnly = usePortalOnly();
  const unread = useUnreadCount().data?.count ?? 0;
  const system = useSystemInfo().data;
  const licenseProblem = system && system.deploymentMode === 'OnPrem' && system.licenseState !== 'Valid' ? system : null;

  return (
    <>
      <a className="skip-link" href="#main">
        {t('common.skipToContent')}
      </a>
      <header className="topbar">
        <strong className="brand">{t('app.title')}</strong>
        <nav aria-label={t('nav.main')}>
          {can('workorders.work') || offlineSession || !!offline.snapshot ? <NavLink to="/jobs">{t('nav.jobs')}</NavLink> : null}
          {portalOnly ? <NavLink to="/portal">{t('nav.portal')}</NavLink> : null}
          {can('locations.read') ? <NavLink to="/locations">{t('nav.locations')}</NavLink> : null}
          {can('assets.read') ? <NavLink to="/assets">{t('nav.assets')}</NavLink> : null}
          {can('workorders.read') || can('workorders.request') ? <NavLink to="/work-orders">{t('nav.workOrders')}</NavLink> : null}
          {can('inventory.read') ? <NavLink to="/inventory">{t('nav.inventory')}</NavLink> : null}
          {can('purchasing.read') || can('purchasing.request') ? <NavLink to="/purchasing">{t('nav.purchasing')}</NavLink> : null}
          {can('reports.read') ? <NavLink to="/reports">{t('nav.reports')}</NavLink> : null}
          {can('compliance.read') || can('permits.read') || can('permits.request') ? <NavLink to="/compliance">{t('nav.compliance')}</NavLink> : null}
          {can('projects.read') ? <NavLink to="/projects">{t('nav.projects')}</NavLink> : null}
          {can('leases.read') ? <NavLink to="/leases">{t('nav.leases')}</NavLink> : null}
          {can('energy.read') ? <NavLink to="/energy">{t('nav.energy')}</NavLink> : null}
          {can('visitors.host') ? <NavLink to="/visitors">{t('nav.visitors')}</NavLink> : null}
          {can('bookings.read') ? <NavLink to="/bookings">{t('nav.bookings')}</NavLink> : null}
          {can('spaces.read') ? <NavLink to="/spaces">{t('nav.spaces')}</NavLink> : null}
          {can('integration.read') ? <NavLink to="/integrations">{t('nav.integrations')}</NavLink> : null}
          {can('vendors.read') ? <NavLink to="/vendors">{t('nav.vendors')}</NavLink> : null}
          {can('pm.read') ? <NavLink to="/pm">{t('nav.pm')}</NavLink> : null}
          {can('locations.manage') || can('assets.manage') ? <NavLink to="/import">{t('nav.import')}</NavLink> : null}
          {can('assets.manage') || can('customfields.manage') || can('workorders.manage') ? <NavLink to="/setup">{t('nav.setup')}</NavLink> : null}
          {can('users.read') ? <NavLink to="/users">{t('nav.users')}</NavLink> : null}
          {can('audit.read') || can('license.read') || can('reports.manage') || can('users.read') || can('workorders.manage') ? <NavLink to="/admin">{t('nav.admin')}</NavLink> : null}
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
      {licenseProblem ? (
        <div className={licenseProblem.licensed ? 'banner' : 'banner error'} role={licenseProblem.licensed ? 'status' : 'alert'}>
          {t(`license.banner.${licenseProblem.licenseState}`, { date: date(licenseProblem.licenseGraceEndsAt ?? licenseProblem.licenseExpiresAt) })}{' '}
          {can('license.read') ? <NavLink to="/admin/license">{t('license.open')}</NavLink> : null}
        </div>
      ) : null}
      <SyncStatus />
      <main id="main" tabIndex={-1}>
        {me.error instanceof ApiError && me.error.isForbidden ? (
          <section>
            {me.error.code === 'license.user_limit' ? (
              <>
                <h1>{t('license.userLimitTitle')}</h1>
                <p>{t('license.userLimit')}</p>
              </>
            ) : (
              <>
                <h1>{t('users.deactivatedAccount')}</h1>
                <p>{t('users.deactivatedHelp')}</p>
              </>
            )}
          </section>
        ) : (
          <Outlet />
        )}
      </main>
    </>
  );
}
