import { useTranslation } from 'react-i18next';
import { useLicense } from '../api/hooks';
import { Badge, QueryState } from '../components/ui';
import { usePrefs } from '../lib/prefs';

/** What this installation is licensed for. Read-only: a license is installed by replacing its file, not through the web. */
export function LicensePage() {
  const { t } = useTranslation();
  const { date } = usePrefs();
  const license = useLicense();
  const l = license.data;
  const tone = l?.state === 'Valid' ? 'ok' : l?.state === 'Grace' ? 'warn' : 'bad';
  return (
    <section>
      <h1>{t('license.title')}</h1>
      <p className="muted">{t('license.intro')}</p>
      <QueryState query={license}>
        {l ? (
          <>
            <p role="status">
              <Badge tone={tone}>{t(`license.state.${l.state}`)}</Badge> {l.detail ? <bdi>{l.detail}</bdi> : null}
            </p>
            <dl className="facts">
              <dt>{t('license.edition')}</dt><dd><bdi>{l.edition}</bdi></dd>
              {l.customer ? (<><dt>{t('license.customer')}</dt><dd><bdi>{l.customer}</bdi></dd></>) : null}
              {l.licenseId ? (<><dt>{t('license.id')}</dt><dd><bdi>{l.licenseId}</bdi></dd></>) : null}
              {l.expiresAt ? (<><dt>{t('license.expires')}</dt><dd>{date(l.expiresAt)}</dd></>) : null}
              {l.graceEndsAt ? (<><dt>{t('license.graceEnds')}</dt><dd>{date(l.graceEndsAt)}</dd></>) : null}
              <dt>{t('license.users')}</dt>
              <dd>{l.maxUsers == null ? t('license.unlimited') : t('license.usersOf', { active: l.activeUsers ?? 0, max: l.maxUsers })}</dd>
            </dl>
            <h2>{t('license.modules')}</h2>
            <ul className="plain">
              {l.allModules.map((m) => {
                const on = l.modules.includes('*') || l.modules.includes(m);
                return (
                  <li key={m}>
                    {t(`license.module.${m}`)} <Badge tone={on ? 'ok' : 'neutral'}>{on ? t('license.included') : t('license.notIncluded')}</Badge>
                  </li>
                );
              })}
            </ul>
            <h2>{t('license.renewTitle')}</h2>
            <p>{t('license.renewHelp')}</p>
          </>
        ) : null}
      </QueryState>
    </section>
  );
}
