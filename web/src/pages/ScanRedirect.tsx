import { useEffect, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import type { Asset } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBanner, Loading } from '../components/ui';

/** What a scanned QR label opens: /a/<tag> finds the asset and goes to it. Signing in first (if needed) returns here. */
export function ScanRedirectPage() {
  const { tag } = useParams();
  const { t } = useTranslation();
  const { api } = useAuth();
  const [target, setTarget] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    let cancelled = false;
    api.get<Asset>(`/assets/by-tag/${encodeURIComponent(tag ?? '')}`).then(
      (asset) => !cancelled && setTarget(`/assets/${asset.id}`),
      (e) => !cancelled && setError(e),
    );
    return () => {
      cancelled = true;
    };
  }, [api, tag]);

  if (target) return <Navigate to={target} replace />;
  if (error) {
    return (
      <section className="narrow">
        <h1>{t('scan.title')}</h1>
        {error instanceof ApiError && error.isNotFound ? <p className="banner error" role="alert">{t('scan.notFound')}</p> : <ErrorBanner error={error} />}
        <Link to="/assets" className="button">{t('nav.assets')}</Link>
      </section>
    );
  }

  return <Loading />;
}
