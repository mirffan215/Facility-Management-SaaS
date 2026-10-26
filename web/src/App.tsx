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
import { UsersPage } from './pages/Users';
import { ScanRedirectPage } from './pages/ScanRedirect';

function Home() {
  const { can, loaded } = usePermissions();
  if (!loaded) return <Loading />;
  if (can('assets.read')) return <Navigate to="/assets" replace />;
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

export function App() {
  const { status, returnTo } = useAuth();
  const navigate = useNavigate();

  // After the sign-in callback, drop the ?code=... URL and go back to where the person was.
  useEffect(() => {
    if (status === 'signedIn' && window.location.pathname === '/auth/callback') navigate(returnTo, { replace: true });
  }, [status, returnTo, navigate]);

  if (status === 'loading') return <Loading />;
  if (status !== 'signedIn') return <SignIn />;

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
        <Route path="users" element={<UsersPage />} />
        <Route path="a/:tag" element={<ScanRedirectPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
