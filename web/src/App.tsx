import { Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useAuth } from './auth/AuthContext';
import { Layout } from './components/Layout';
import { Loading } from './components/ui';
import { usePermissions, usePortalOnly } from './api/hooks';
import { SignIn } from './pages/SignIn';
import { LocationsPage } from './pages/Locations';
import { AssetsPage } from './pages/Assets';
import { AssetDetailPage } from './pages/AssetDetail';
import { AssetCreatePage } from './pages/AssetCreate';
import { ImportPage } from './pages/Import';
import { SetupPage } from './pages/Setup';
import { AdminPage } from './pages/Admin';
import { AuditPage } from './pages/Audit';
import { CompliancePage } from './pages/Compliance';
import { AiSettingsPage } from './pages/AiSettings';
import { LeasesPage } from './pages/Leases';
import { ProjectsPage } from './pages/Projects';
import { EnergyPage } from './pages/Energy';
import { LicensePage } from './pages/License';
import { NotificationsPage } from './pages/Notifications';
import { WorkOrdersPage } from './pages/WorkOrders';
import { WorkOrderCreatePage } from './pages/WorkOrderCreate';
import { WorkOrderDetailPage } from './pages/WorkOrderDetail';
import { UsersPage } from './pages/Users';
import { JobsPage } from './pages/Jobs';
import { JobPage } from './pages/Job';
import { PmPage } from './pages/Pm';
import { InventoryPage } from './pages/Inventory';
import { VendorsPage } from './pages/Vendors';
import { PurchasingPage } from './pages/Purchasing';
import { IntegrationsPage } from './pages/Integrations';
import { ReportsPage } from './pages/Reports';
import { VisitorsPage } from './pages/Visitors';
import { VisitPassPage } from './pages/VisitPass';
import { BookingsPage } from './pages/Bookings';
import { SpacesPage } from './pages/Spaces';
import { PortalPage } from './pages/Portal';
import { AcceptInvitationPage } from './pages/AcceptInvitation';
import { ScanRedirectPage } from './pages/ScanRedirect';

function Home() {
  const { can, loaded } = usePermissions();
  const portalOnly = usePortalOnly();
  if (!loaded) return <Loading />;
  if (portalOnly) return <Navigate to="/portal" replace />; // a vendor's people have the portal and nothing else
  if (can('workorders.work') && !can('workorders.manage')) return <Navigate to="/jobs" replace />; // field staff start the day at their jobs
  if (can('assets.read')) return <Navigate to="/assets" replace />;
  if (can('workorders.read') || can('workorders.request')) return <Navigate to="/work-orders" replace />;
  if (can('locations.read')) return <Navigate to="/locations" replace />;
  return <NoAccess />;
}

function NoAccess() {
  const { t } = useTranslation();
  return (
    <section>
      <h1>{t('errors.noAccessTitle')}</h1>
      <p>{t('errors.noAccess')}</p>
    </section>
  );
}

function NotFound() {
  const { t } = useTranslation();
  return (
    <section>
      <h1>{t('errors.pageNotFound')}</h1>
    </section>
  );
}

function OfflineOnly() {
  const { t } = useTranslation();
  return (
    <section>
      <h1>{t('offline.limitedTitle')}</h1>
      <p>{t('offline.limited')}</p>
    </section>
  );
}

export function App() {
  const { status, returnTo, offlineSession } = useAuth();
  const navigate = useNavigate();
  const portalOnly = usePortalOnly();

  // After the sign-in callback, drop the ?code=... URL and go back to where the person was.
  useEffect(() => {
    if (status === 'signedIn' && window.location.pathname === '/auth/callback') navigate(returnTo, { replace: true });
  }, [status, returnTo, navigate]);

  if (window.location.pathname.startsWith('/visit/')) return <VisitPassPage />; // a visitor's own pass: no sign-in
  if (status === 'loading') return <Loading />;
  if (status !== 'signedIn') return <SignIn />;
  if (window.location.pathname === '/portal/accept') return <AcceptInvitationPage />; // may belong to no organisation yet

  // Signed in on this phone only (expired session, no connection): the saved jobs, and nothing that needs the server.
  if (offlineSession) {
    return (
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<Navigate to="/jobs" replace />} />
          <Route path="jobs" element={<JobsPage />} />
          <Route path="jobs/:id" element={<JobPage />} />
          <Route path="*" element={<OfflineOnly />} />
        </Route>
      </Routes>
    );
  }

  // A vendor's person has the portal and notifications; any other address leads back to it.
  if (portalOnly) {
    return (
      <Routes>
        <Route element={<Layout />}>
          <Route path="auth/callback" element={<Loading />} />
          <Route path="portal" element={<PortalPage />} />
          <Route path="notifications" element={<NotificationsPage />} />
          <Route path="*" element={<Navigate to="/portal" replace />} />
        </Route>
      </Routes>
    );
  }

  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Home />} />
        <Route path="auth/callback" element={<Loading />} />
        <Route path="locations" element={<LocationsPage />} />
        <Route path="assets" element={<AssetsPage />} />
        <Route path="assets/new" element={<AssetCreatePage />} />
        <Route path="assets/:id" element={<AssetDetailPage />} />
        <Route path="import" element={<ImportPage />} />
        <Route path="setup" element={<SetupPage />} />
        <Route path="jobs" element={<JobsPage />} />
        <Route path="jobs/:id" element={<JobPage />} />
        <Route path="work-orders" element={<WorkOrdersPage />} />
        <Route path="work-orders/new" element={<WorkOrderCreatePage />} />
        <Route path="work-orders/:id" element={<WorkOrderDetailPage />} />
        <Route path="pm" element={<PmPage />} />
        <Route path="inventory" element={<InventoryPage />} />
        <Route path="vendors" element={<VendorsPage />} />
        <Route path="purchasing" element={<PurchasingPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="compliance" element={<CompliancePage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="leases" element={<LeasesPage />} />
        <Route path="energy" element={<EnergyPage />} />
        <Route path="visitors" element={<VisitorsPage />} />
        <Route path="bookings" element={<BookingsPage />} />
        <Route path="spaces" element={<SpacesPage />} />
        <Route path="integrations" element={<IntegrationsPage />} />
        <Route path="portal" element={<PortalPage />} />
        <Route path="notifications" element={<NotificationsPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="admin" element={<AdminPage />} />
        <Route path="audit" element={<AuditPage />} />
        <Route path="admin/license" element={<LicensePage />} />
        <Route path="admin/ai" element={<AiSettingsPage />} />
        <Route path="a/:tag" element={<ScanRedirectPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
