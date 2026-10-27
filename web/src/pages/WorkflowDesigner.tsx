import { useEffect, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useWorkflow, useWorkflowHistory } from '../api/hooks';
import type { WorkPriority, WorkflowRules, WorkflowVersion } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const PRIORITIES: WorkPriority[] = ['Low', 'Medium', 'High', 'Urgent'];

function toggle(list: WorkPriority[], p: WorkPriority, on: boolean): WorkPriority[] {
  return PRIORITIES.filter((x) => (x === p ? on : list.includes(x)));
}

/** The workflow designer: the optional steps and evidence an organisation uses, inside rules that cannot be switched off. */
export function WorkflowSection() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const client = useQueryClient();
  const priorityLabel = useEnumLabel('workPriority');
  const workflow = useWorkflow();
  const history = useWorkflowHistory(true);
  const [rules, setRules] = useState<WorkflowRules | null>(null);
  const [note, setNote] = useState('');
  const [restoring, setRestoring] = useState<WorkflowVersion | null>(null);

  const loaded = workflow.data;
  useEffect(() => {
    if (loaded) setRules(loaded.rules);
  }, [loaded]);

  const refresh = async () => {
    await client.invalidateQueries({ queryKey: ['workflow'] });
    await client.invalidateQueries({ queryKey: ['work-settings'] });
  };
  const publish = useMutation({
    mutationFn: () => api.post('/work-orders/workflow/publish', { rules, note: note.trim(), expectedVersion: loaded?.version }),
    onSuccess: async () => { setNote(''); await refresh(); },
  });
  const restore = useMutation({
    mutationFn: (v: { version: number; note: string }) => api.post('/work-orders/workflow/restore', { version: v.version, note: v.note, expectedVersion: loaded?.version }),
    onSuccess: async () => { setRestoring(null); await refresh(); },
  });
  const errors = publish.error instanceof ApiError ? publish.error.fieldErrors : {};
  const dirty = !!rules && !!loaded && JSON.stringify(rules) !== JSON.stringify(loaded.rules);
  const set = (next: Partial<WorkflowRules>) => setRules((r) => (r ? { ...r, ...next } : r));

  return (
    <>
      <h2>{t('workflow.title')}</h2>
      <p className="muted">{t('workflow.intro')}</p>
      <QueryState query={workflow}>
        {loaded && rules ? (
          <>
            <p>
              {loaded.version === 0 ? t('workflow.defaults') : t('workflow.current', { version: loaded.version, when: date(loaded.publishedAt, true) })}
            </p>
            <form onSubmit={(e) => { e.preventDefault(); publish.mutate(); }} noValidate aria-label={t('workflow.title')}>
              <fieldset>
                <legend>{t('workflow.approval')}</legend>
                <label className="check">
                  <input type="checkbox" checked={rules.requireApproval} onChange={(e) => set({ requireApproval: e.target.checked, waivedApprovalPriorities: e.target.checked ? rules.waivedApprovalPriorities : [] })} />
                  <span>{t('workflow.requireApproval')} <small className="muted">{t('workflow.requireApprovalHint')}</small></span>
                </label>
                <div role="group" aria-label={t('workflow.waived')}>
                  <span className="muted">{t('workflow.waived')}</span>{' '}
                  {PRIORITIES.map((p) => (
                    <label key={p} className="check inline">
                      <input type="checkbox" disabled={!rules.requireApproval} checked={rules.waivedApprovalPriorities.includes(p)}
                        onChange={(e) => set({ waivedApprovalPriorities: toggle(rules.waivedApprovalPriorities, p, e.target.checked) })} />
                      <span>{priorityLabel(p)}</span>
                    </label>
                  ))}
                </div>
                {errors.waivedApprovalPriorities?.[0] ? <p role="alert" className="error">{errors.waivedApprovalPriorities[0]}</p> : null}
              </fieldset>
              <fieldset>
                <legend>{t('workflow.verification')}</legend>
                <label className="check">
                  <input type="checkbox" checked={rules.requireVerification} onChange={(e) => set({ requireVerification: e.target.checked, verificationPriorities: e.target.checked ? rules.verificationPriorities : [] })} />
                  <span>{t('workflow.requireVerification')} <small className="muted">{t('workflow.requireVerificationHint')}</small></span>
                </label>
                <div role="group" aria-label={t('workflow.verifiedPriorities')}>
                  <span className="muted">{t('workflow.verifiedPriorities')}</span>{' '}
                  {PRIORITIES.map((p) => (
                    <label key={p} className="check inline">
                      <input type="checkbox" disabled={!rules.requireVerification} checked={rules.verificationPriorities.includes(p)}
                        onChange={(e) => set({ verificationPriorities: toggle(rules.verificationPriorities, p, e.target.checked) })} />
                      <span>{priorityLabel(p)}</span>
                    </label>
                  ))}
                </div>
                {errors.verificationPriorities?.[0] ? <p role="alert" className="error">{errors.verificationPriorities[0]}</p> : null}
              </fieldset>
              <fieldset>
                <legend>{t('workflow.evidence')}</legend>
                <label className="check"><input type="checkbox" checked={rules.requireCompletionSignature} onChange={(e) => set({ requireCompletionSignature: e.target.checked })} /><span>{t('workflow.signature')}</span></label>
                <label className="check"><input type="checkbox" checked={rules.requireTimeOnCompletion} onChange={(e) => set({ requireTimeOnCompletion: e.target.checked })} /><span>{t('workflow.time')}</span></label>
                <label className="check"><input type="checkbox" checked={rules.requirePhotoOnCompletion} onChange={(e) => set({ requirePhotoOnCompletion: e.target.checked })} /><span>{t('workflow.photo')}</span></label>
              </fieldset>
              <Field label={t('workflow.note')} hint={t('workflow.noteHint')} error={errors.note?.[0]} required>
                <textarea value={note} onChange={(e) => setNote(e.target.value)} rows={2} maxLength={500} />
              </Field>
              <ErrorBanner error={Object.keys(errors).length ? null : publish.error} />
              <button type="submit" className="primary" disabled={publish.isPending || !dirty || note.trim().length === 0}>{t('workflow.publish')}</button>
            </form>

            <h3>{t('workflow.fixedTitle')}</h3>
            <p className="muted">{t('workflow.fixedHint')}</p>
            <ul>
              {loaded.fixedRules.map((r) => <li key={r}>{t(`workflow.fixed.${r}`, { defaultValue: r })}</li>)}
            </ul>

            <h3>{t('workflow.history')}</h3>
            <QueryState query={history}>
              {history.data && history.data.length === 0 ? <p className="muted">{t('workflow.noHistory')}</p> : null}
              {history.data && history.data.length > 0 ? (
                <div className="table-wrap" tabIndex={0} role="region" aria-label={t('workflow.history')}>
                  <table>
                    <caption className="visually-hidden">{t('workflow.history')}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{t('workflow.version')}</th>
                        <th scope="col">{t('workflow.when')}</th>
                        <th scope="col">{t('workflow.who')}</th>
                        <th scope="col">{t('workflow.note')}</th>
                        <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                      </tr>
                    </thead>
                    <tbody>
                      {history.data.map((v) => (
                        <tr key={v.version}>
                          <th scope="row">{v.version} {v.version === loaded.version ? <Badge tone="ok">{t('workflow.live')}</Badge> : null}</th>
                          <td>{date(v.changedAt, true)}</td>
                          <td><bdi>{v.changedByName ?? t('workflow.system')}</bdi></td>
                          <td><bdi>{v.note}</bdi></td>
                          <td>
                            {v.version !== loaded.version ? (
                              <button type="button" onClick={() => setRestoring(v)} aria-label={t('workflow.restoreVersion', { version: v.version })}>{t('common.restore')}</button>
                            ) : null}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </QueryState>
          </>
        ) : null}
      </QueryState>
      {restoring ? (
        <ReasonDialog
          title={t('workflow.restoreTitle', { version: restoring.version })}
          label={t('workflow.note')}
          confirmLabel={t('common.restore')}
          busy={restore.isPending}
          error={restore.error}
          onConfirm={(reason) => restore.mutate({ version: restoring.version, note: reason })}
          onCancel={() => { setRestoring(null); restore.reset(); }}
        />
      ) : null}
    </>
  );
}
