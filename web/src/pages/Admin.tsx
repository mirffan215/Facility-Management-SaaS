import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { usePermissions } from '../api/hooks';

interface Entry { key: string; to: string; any: string[] }

export const ADMIN_ENTRIES: Entry[] = [
  { key: 'users', to: '/users', any: ['users.read'] },
  { key: 'setup', to: '/setup', any: ['assets.manage', 'customfields.manage', 'workorders.manage'] },
  { key: 'workflow', to: '/setup?section=workflow', any: ['workorders.manage'] },
  { key: 'sla', to: '/setup?section=sla', any: ['workorders.manage'] },
  { key: 'feed', to: '/reports?tab=feed', any: ['reports.manage'] },
  { key: 'accounting', to: '/integrations', any: ['integration.read'] },
  { key: 'visitors', to: '/visitors', any: ['visitors.security'] },
  { key: 'audit', to: '/audit', any: ['audit.read'] },
  { key: 'license', to: '/admin/license', any: ['license.read'] },
];

/** One place to find every administration area; each card shows only if the person may open it. */
export function AdminPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const shown = ADMIN_ENTRIES.filter((e) => e.any.some(can));
  return (
    <section>
      <h1>{t('admin.title')}</h1>
      <p className="muted">{t('admin.intro')}</p>
      {shown.length === 0 ? <p className="muted">{t('admin.none')}</p> : null}
      <ul className="cards plain">
        {shown.map((e) => (
          <li key={e.key} className="card">
            <h2><Link to={e.to}>{t(`admin.${e.key}`)}</Link></h2>
            <p className="muted">{t(`admin.${e.key}Hint`)}</p>
          </li>
        ))}
      </ul>
    </section>
  );
}
