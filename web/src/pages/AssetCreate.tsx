import { useNavigate, useSearchParams } from 'react-router-dom';
import { Can } from '../components/ui';
import { AssetForm } from '../components/AssetForm';
import { useTranslation } from 'react-i18next';

export function AssetCreatePage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const { t } = useTranslation();
  return (
    <section className="narrow">
      <Can permission="assets.manage">
        <AssetForm initialLocationId={params.get('locationId') ?? undefined} onDone={(saved) => navigate(saved ? `/assets/${saved.id}` : '/assets')} />
      </Can>
      <Can permission="assets.read">
        <span className="visually-hidden">{t('nav.assets')}</span>
      </Can>
    </section>
  );
}
