import { UserManager, WebStorageStateStore } from 'oidc-client-ts';
import type { ProviderInfo } from '../api/types';

/**
 * Authorization-code flow with PKCE against the provider the API advertises. The access token lives in sessionStorage (cleared when the
 * tab closes); it is never written to localStorage. The API validates the token, so nothing here is a security boundary.
 */
export function createManager(provider: ProviderInfo): UserManager {
  if (!provider.clientId) throw new Error(`Identity provider '${provider.name}' has no client id configured.`);
  return new UserManager({
    authority: provider.authority,
    client_id: provider.clientId,
    redirect_uri: `${window.location.origin}/auth/callback`,
    post_logout_redirect_uri: window.location.origin,
    response_type: 'code',
    scope: provider.scope ?? 'openid profile email',
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    automaticSilentRenew: false,
    loadUserInfo: false,
  });
}

export const PROVIDER_KEY = 'fms.idp';
export const RETURN_KEY = 'fms.returnTo';

export function safeReturnPath(value: string | null | undefined): string {
  // Only same-origin paths: never follow a returnTo that could send the user (and the code) elsewhere.
  return value && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/';
}
