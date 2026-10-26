import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useMe, useRoles, useUsers } from '../api/hooks';
import type { RoleSummary, UserSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, Can, ErrorBanner, QueryState } from '../components/ui';
import { usePrefs } from '../lib/prefs';

export function UsersPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const client = useQueryClient();
  const me = useMe();
  const users = useUsers();
  const roles = useRoles();
  const [editing, setEditing] = useState<UserSummary | null>(null);
  const [toggling, setToggling] = useState<UserSummary | null>(null);

  const roleName = (id: string) => roles.data?.find((r) => r.id === id)?.name ?? '';
  const toggle = useMutation({
    mutationFn: (u: UserSummary) => api.post(`/users/${u.id}/${u.isActive ? 'deactivate' : 'activate'}`),
    onSuccess: async () => {
      setToggling(null);
      await client.invalidateQueries({ queryKey: ['users'] });
    },
  });

  return (
    <section>
      <h1>{t('users.title')}</h1>
      <p className="muted">{t('users.intro')}</p>
      <QueryState query={users}>
        <div className="table-wrap">
          <table>
            <caption className="visually-hidden">{t('users.title')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('users.person')}</th>
                <th scope="col">{t('users.roles')}</th>
                <th scope="col">{t('users.lastSignIn')}</th>
                <th scope="col">{t('assets.status')}</th>
                <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {users.data?.map((u) => {
                const isMe = u.id === me.data?.userId;
                return (
                  <tr key={u.id}>
                    <td>
                      <bdi>{u.displayName ?? u.email ?? u.id}</bdi> {isMe ? <Badge tone="neutral">{t('users.you')}</Badge> : null}
                      {u.email && u.displayName ? <div className="muted"><bdi>{u.email}</bdi></div> : null}
                    </td>
                    <td>{u.roleIds.length === 0 ? <span className="muted">{t('users.noRoles')}</span> : u.roleIds.map(roleName).filter(Boolean).join(', ')}</td>
                    <td>{u.lastSignInAt ? date(u.lastSignInAt, true) : '—'}</td>
                    <td><Badge tone={u.isActive ? 'ok' : 'bad'}>{u.isActive ? t('users.active') : t('users.deactivated')}</Badge></td>
                    <td className="actions">
                      <Can permission="users.manage">
                        <button type="button" disabled={isMe} onClick={() => setEditing(u)}>{t('users.editRoles')}</button>
                        <button type="button" disabled={isMe && u.isActive} onClick={() => setToggling(u)}>{u.isActive ? t('users.deactivate') : t('users.reactivate')}</button>
                      </Can>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </QueryState>

      <h2>{t('users.rolesTitle')}</h2>
      <QueryState query={roles}>
        <div className="cards">
          {roles.data?.map((r) => (
            <details key={r.id} className="card">
              <summary><strong>{r.name}</strong> <span className="muted">· {t('users.permissionCount', { count: r.permissions.length })}</span></summary>
              {r.permissions.length === 0 ? <p className="muted">{t('users.noPermissions')}</p> : <ul>{r.permissions.map((p) => <li key={p}><code>{p}</code></li>)}</ul>}
            </details>
          ))}
        </div>
      </QueryState>

      {editing && roles.data ? <RolesDialog user={editing} roles={roles.data} onClose={() => setEditing(null)} /> : null}
      {toggling ? (
        <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && setToggling(null)}>
          <div className="dialog" role="dialog" aria-modal="true" aria-label={toggling.isActive ? t('users.deactivate') : t('users.reactivate')}>
            <h2>{toggling.isActive ? t('users.deactivateTitle', { name: toggling.displayName ?? toggling.email ?? '' }) : t('users.reactivateTitle', { name: toggling.displayName ?? toggling.email ?? '' })}</h2>
            <p>{toggling.isActive ? t('users.deactivateWarning') : t('users.reactivateInfo')}</p>
            <ErrorBanner error={toggle.error} />
            <div className="actions">
              <button type="button" onClick={() => setToggling(null)}>{t('common.cancel')}</button>
              <button type="button" className="primary" disabled={toggle.isPending} onClick={() => toggle.mutate(toggling)} autoFocus>{t('common.confirm')}</button>
            </div>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function RolesDialog({ user, roles, onClose }: { user: UserSummary; roles: RoleSummary[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [selected, setSelected] = useState<Set<string>>(new Set(user.roleIds));

  const save = useMutation({
    mutationFn: () => api.put(`/users/${user.id}/roles`, { roleIds: [...selected] }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['users'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('users.editRoles')}>
        <h2>{t('users.rolesFor', { name: user.displayName ?? user.email ?? '' })}</h2>
        <fieldset>
          <legend>{t('users.roles')}</legend>
          {roles.map((r) => (
            <label key={r.id} className="check">
              <input
                type="checkbox"
                checked={selected.has(r.id)}
                onChange={(e) => setSelected((prev) => { const next = new Set(prev); if (e.target.checked) next.add(r.id); else next.delete(r.id); return next; })}
              />
              <span><strong>{r.name}</strong> <span className="muted">· {t('users.permissionCount', { count: r.permissions.length })}</span></span>
            </label>
          ))}
        </fieldset>
        <ErrorBanner error={error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="button" className="primary" disabled={save.isPending} onClick={() => save.mutate()}>{t('common.save')}</button>
        </div>
      </div>
    </div>
  );
}
