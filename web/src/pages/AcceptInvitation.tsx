import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';

const TOKEN_KEY = 'fms.invitation';

/** Called before anything else renders: the invitation token travels in the link's fragment, which a sign-in redirect would drop, so keep it for the return trip. */
export function stashInvitationToken() {
  if (window.location.pathname !== '/portal/accept') return;
  const token = new URLSearchParams(window.location.hash.slice(1)).get('token');
  if (!token) return;
  try {
    sessionStorage.setItem(TOKEN_KEY, token);
  } catch {
    // Storage unavailable: the link still works if the person is already signed in.
  }
  window.history.replaceState(null, '', window.location.pathname); // the token leaves the address bar and history
}

function readToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

/** Shown to someone who followed an invitation link and is signed in. They may belong to no organisation yet, so nothing here calls an API that needs one. */
export function AcceptInvitationPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const [state, setState] = useState<'working' | 'done' | 'failed'>('working');
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    const token = readToken();
    if (!token) {
      setState('failed');
      setProblem(t('portal.accept.missing'));
      return;
    }
    let cancelled = false;
    api
      .post('/portal/invitations/accept', { token })
      .then(() => {
        try {
          sessionStorage.removeItem(TOKEN_KEY);
        } catch {
          // Nothing to clean up.
        }
        if (!cancelled) setState('done');
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setState('failed');
        setProblem(e instanceof ApiError && e.status !== 0 ? e.title : t('portal.accept.network'));
      });
    return () => {
      cancelled = true;
    };
  }, [api, t]);

  return (
    <main className="signin">
      <h1>{t('portal.accept.title')}</h1>
      {state === 'working' ? <p role="status">{t('portal.accept.working')}</p> : null}
      {state === 'failed' ? <p className="banner error" role="alert">{problem}</p> : null}
      {state === 'done' ? (
        <>
          <p role="status">{t('portal.accept.done')}</p>
          <a className="button" href="/">{t('portal.accept.open')}</a>
        </>
      ) : null}
    </main>
  );
}
