import { Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useAuth } from './auth/AuthContext';
import { Layout } from './components/Layout';
import { Loading } from './components/ui';
import { usePermissions } from './api/hooks';
import { SignIn } from './pages/SignIn';
import { LocationsPage } from './pages/Locations';
import { AssetsPage } from './pages/Assets';
import { AssetDetailPage } from './pages/AssetDetail';
import { AssetCreatePage } from './pages/AssetCreate';
import { ImportPage } from './pages/Import';
import { SetupPage } from './pages/Setup';
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
import { ScanRedirectPage } from './pages/ScanRedirect';

function Home() {
  const { can, loaded } = usePermissions();
  if (!loaded) return <Loading />;
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

  // After the sign-in callback, drop the ?code=... URL and go back to where the person was.
  useEffect(() => {
    if (status === 'signedIn' && window.location.pathname === '/auth/callback') navigate(returnTo, { replace: true });
  }, [status, returnTo, navigate]);

  if (status === 'loading') return <Loading />;
  if (status !== 'signedIn') return <SignIn />;

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
        <Route path="integrations" element={<IntegrationsPage />} />
        <Route path="notifications" element={<NotificationsPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="a/:tag" element={<ScanRedirectPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
