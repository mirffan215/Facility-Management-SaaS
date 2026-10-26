import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { useOffline } from './OfflineProvider';

/** One line that says whether the phone is connected and whether anything is waiting. Shown only when there is something to say. */
export function SyncStatus() {
  const { t } = useTranslation();
  const { providers, signIn, offlineSession } = useAuth();
  const { online, state, syncNow } = useOffline();
  if (online && !offlineSession && state.pending === 0 && state.failed === 0 && !state.authRequired) return null;

  const provider = providers.find((p) => p.name === localStorage.getItem('fms.idp')) ?? providers[0];
  return (
    <div className={`sync-status ${online ? '' : 'offline'}`} role="status">
      <span>
        {online ? t('offline.online') : t('offline.offline')}
        {state.pending > 0 ? ` · ${t('offline.waiting', { count: state.pending })}` : ''}
        {state.failed > 0 ? ` · ${t('offline.issues', { count: state.failed })}` : ''}
      </span>
      {online && state.pending > 0 && !state.authRequired && !offlineSession ? <button type="button" disabled={state.syncing} onClick={() => void syncNow()}>{t('offline.syncNow')}</button> : null}
      {(state.authRequired || (offlineSession && online)) && provider ? <button type="button" className="primary" onClick={() => void signIn(provider.name)}>{t('offline.signInToSend')}</button> : null}
    </div>
  );
}
