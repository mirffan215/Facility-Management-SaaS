import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { User, UserManager } from 'oidc-client-ts';
import { createApi, type Api } from '../api/client';
import type { ProviderInfo } from '../api/types';
import { createManager, PROVIDER_KEY, RETURN_KEY, safeReturnPath } from './oidc';

export type AuthStatus = 'loading' | 'signedOut' | 'signedIn' | 'unreachable' | 'failed';

interface AuthValue {
  status: AuthStatus;
  providers: ProviderInfo[];
  /** Why sign-in failed or the session ended, when it did. */
  reason: 'expired' | 'callback' | null;
  displayName: string | null;
  api: Api;
  signIn: (providerName: string) => Promise<void>;
  signOut: () => Promise<void>;
  /** Where to go after the sign-in callback completes. */
  returnTo: string;
}

const AuthContext = createContext<AuthValue | null>(null);

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [reason, setReason] = useState<AuthValue['reason']>(null);
  const [user, setUser] = useState<User | null>(null);
  const [returnTo, setReturnTo] = useState('/');
  const manager = useRef<UserManager | null>(null);
  const token = useRef<string | null>(null);
  token.current = user && !user.expired ? user.access_token : null;

  const endSession = useCallback((why: AuthValue['reason']) => {
    void manager.current?.removeUser();
    setUser(null);
    setReason(why);
    setStatus('signedOut');
  }, []);

  const api = useMemo(
    () => createApi({ getToken: () => token.current, onUnauthorized: () => endSession('expired') }),
    [endSession],
  );

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let list: ProviderInfo[];
      try {
        const response = await fetch('/api/v1/auth/providers');
        if (!response.ok) throw new Error(String(response.status));
        list = (await response.json()) as ProviderInfo[];
      } catch {
        if (!cancelled) setStatus('unreachable');
        return;
      }

      if (cancelled) return;
      setProviders(list);
      const chosen = list.find((p) => p.name === localStorage.getItem(PROVIDER_KEY)) ?? (list.length === 1 ? list[0] : undefined);
      if (!chosen?.clientId) {
        setStatus('signedOut');
        return;
      }

      const created = createManager(chosen);
      manager.current = created;
      created.events.addUserLoaded((renewed) => {
        if (!cancelled) setUser(renewed);
      });
      created.events.addSilentRenewError(() => {
        // A failed renewal only matters once the token can no longer be used; a brief network blip should not sign anyone out.
        void created.getUser().then((current) => {
          if (!cancelled && (!current || current.expired)) endSession('expired');
        });
      });
      try {
        if (window.location.pathname === '/auth/callback') {
          const signedIn = await manager.current.signinRedirectCallback();
          if (cancelled) return;
          setReturnTo(safeReturnPath(sessionStorage.getItem(RETURN_KEY)));
          sessionStorage.removeItem(RETURN_KEY);
          setUser(signedIn);
          setStatus('signedIn');
        } else {
          const existing = await manager.current.getUser();
          if (cancelled) return;
          if (existing && !existing.expired) {
            setUser(existing);
            setStatus('signedIn');
          } else {
            if (existing) void manager.current.removeUser();
            setReason(existing ? 'expired' : null);
            setStatus('signedOut');
          }
        }
      } catch {
        if (!cancelled) {
          setReason('callback');
          setStatus('failed');
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [endSession]);

  const signIn = useCallback(
    async (providerName: string) => {
      const provider = providers.find((p) => p.name === providerName);
      if (!provider) return;
      localStorage.setItem(PROVIDER_KEY, provider.name);
      sessionStorage.setItem(RETURN_KEY, window.location.pathname + window.location.search);
      manager.current = createManager(provider);
      await manager.current.signinRedirect();
    },
    [providers],
  );

  const signOut = useCallback(async () => {
    const current = manager.current;
    setUser(null);
    setStatus('signedOut');
    if (!current) return;
    try {
      await current.signoutRedirect();
    } catch {
      // Provider has no end-session endpoint: forgetting the local session is all that can be done.
      await current.removeUser();
    }
  }, []);

  const displayName = (user?.profile.name as string | undefined) ?? (user?.profile.preferred_username as string | undefined) ?? (user?.profile.email as string | undefined) ?? null;

  const value = useMemo<AuthValue>(
    () => ({ status, providers, reason, displayName, api, signIn, signOut, returnTo }),
    [status, providers, reason, displayName, api, signIn, signOut, returnTo],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
