import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import type { PortalAccess, Vendor } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { usePermissions } from '../api/hooks';
import { usePrefs } from '../lib/prefs';
import { Dialog } from './InventoryBits';
import { Badge, ErrorBanner, Field, QueryState } from './ui';

interface Created {
  invitation: { email: string };
  token: string;
  path: string;
}

/** Who from a vendor can use the portal, and the invitations that are out. The invitation link is shown once, here, and never stored in readable form. */
export function PortalAccessDialog({ vendor, onClose }: { vendor: Vendor; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name, date } = usePrefs();
  const client = useQueryClient();
  const [email, setEmail] = useState('');
  const [link, setLink] = useState<{ email: string; url: string } | null>(null);
  const key = ['vendors', vendor.id, 'portal-access'];
  const access = useQuery({ queryKey: key, queryFn: () => api.get<PortalAccess>(`/vendors/${vendor.id}/portal-access`) });
  const refresh = () => client.invalidateQueries({ queryKey: key });
  const invite = useMutation({
    mutationFn: () => api.post<Created>(`/vendors/${vendor.id}/invitations`, { email }),
    onSuccess: async (c) => {
      setLink({ email: c.invitation.email, url: `${window.location.origin}${c.path}#token=${encodeURIComponent(c.token)}` });
      setEmail('');
      await refresh();
    },
  });
  const revoke = useMutation({ mutationFn: (id: string) => api.post(`/vendors/${vendor.id}/invitations/${id}/revoke`), onSuccess: () => refresh() });
  const setActive = useMutation({
    mutationFn: (v: { userId: string; active: boolean }) => api.post(`/vendors/${vendor.id}/portal-users/${v.userId}/active`, { active: v.active }),
    onSuccess: () => refresh(),
  });
  const manage = can('vendors.manage');
  const emailError = invite.error instanceof ApiError ? invite.error.field('email') : undefined;
  return (
    <Dialog title={t('vendorPortal.title', { vendor: name(vendor) })} onClose={onClose} wide>
      <p className="muted">{t('vendorPortal.intro')}</p>
      <ErrorBanner error={emailError ? null : (invite.error ?? revoke.error ?? setActive.error)} />
      {manage ? (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            invite.mutate();
          }}
        >
          <Field label={t('vendorPortal.email')} error={emailError}>
            <input value={email} inputMode="email" autoComplete="off" onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <button type="submit" disabled={invite.isPending || email.trim() === ''}>{t('vendorPortal.invite')}</button>
        </form>
      ) : null}
      {link ? (
        <div className="banner" role="status">
          <strong>{t('vendorPortal.linkTitle')}</strong>
          <p>{t('vendorPortal.linkHelp', { email: link.email })}</p>
          <input readOnly value={link.url} aria-label={t('vendorPortal.linkTitle')} dir="ltr" onFocus={(e) => e.currentTarget.select()} />
        </div>
      ) : null}
      <QueryState query={access}>
        <h3>{t('vendorPortal.people')}</h3>
        {access.data && access.data.users.length === 0 ? <p className="muted">{t('vendorPortal.noPeople')}</p> : null}
        <ul className="plain">
          {(access.data?.users ?? []).map((u) => (
            <li key={u.userId}>
              <bdi>{u.name ?? u.email ?? u.userId}</bdi> {u.email && u.name ? <span className="muted"><bdi>{u.email}</bdi></span> : null}{' '}
              {!u.isActive ? <Badge tone="neutral">{t('vendorPortal.inactive')}</Badge> : null}
              {manage ? (
                <button type="button" disabled={setActive.isPending} onClick={() => setActive.mutate({ userId: u.userId, active: !u.isActive })}>
                  {u.isActive ? t('vendorPortal.deactivate') : t('vendorPortal.reactivate')}
                </button>
              ) : null}
            </li>
          ))}
        </ul>
        <h3>{t('vendorPortal.invitations')}</h3>
        {access.data && access.data.invitations.length === 0 ? <p className="muted">{t('vendorPortal.noInvitations')}</p> : null}
        <ul className="plain">
          {(access.data?.invitations ?? []).map((i) => (
            <li key={i.id}>
              <bdi>{i.email}</bdi> <Badge tone={i.state === 'Open' ? 'warn' : i.state === 'Accepted' ? 'ok' : 'neutral'}>{t(`vendorPortal.state.${i.state}`, { defaultValue: i.state })}</Badge>{' '}
              {i.state === 'Open' ? <span className="muted">{t('vendorPortal.expires', { date: date(i.expiresAt) })}</span> : null}
              {manage && i.state === 'Open' ? (
                <button type="button" disabled={revoke.isPending} onClick={() => revoke.mutate(i.id)}>{t('vendorPortal.revoke')}</button>
              ) : null}
            </li>
          ))}
        </ul>
      </QueryState>
      <div className="actions">
        <button type="button" onClick={onClose}>{t('vendorPortal.close')}</button>
      </div>
    </Dialog>
  );
}
