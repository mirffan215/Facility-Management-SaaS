import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssetClasses } from '../api/hooks';
import type { AssetClass, CustomFieldDefinition, CustomFieldTarget, CustomFieldType } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, Can, ErrorBanner, Field, QueryState, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const SECTIONS = ['classes', 'fields'] as const;

export function SetupPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const section = SECTIONS.find((s) => s === params.get('section')) ?? 'classes';
  return (
    <section>
      <h1>{t('setup.title')}</h1>
      <div className="tabs" role="tablist" aria-label={t('setup.sections')}>
        {SECTIONS.map((s) => (
          <button key={s} role="tab" type="button" aria-selected={section === s} className={section === s ? 'active' : ''} onClick={() => setParams({ section: s })}>
            {t(`setup.${s}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{section === 'classes' ? <ClassesSection /> : <FieldsSection />}</div>
    </section>
  );
}

function ClassesSection() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const client = useQueryClient();
  const classes = useAssetClasses();
  const [code, setCode] = useState('');
  const [nameEn, setNameEn] = useState('');
  const [nameAr, setNameAr] = useState('');
  const [parentId, setParentId] = useState('');

  const create = useMutation({
    mutationFn: () => api.post<AssetClass>('/asset-classes', { code: code.trim(), name: nameEn.trim(), nameAr: nameAr.trim() || null, parentId: parentId || null }),
    onSuccess: async () => {
      setCode(''); setNameEn(''); setNameAr(''); setParentId('');
      await client.invalidateQueries({ queryKey: ['asset-classes'] });
    },
  });
  const archive = useMutation({
    mutationFn: (c: AssetClass) => api.post<AssetClass>(`/asset-classes/${c.id}/archive`, { rowVersion: c.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['asset-classes'] }),
  });
  const errors = create.error instanceof ApiError ? create.error.fieldErrors : {};

  return (
    <>
      <h2>{t('setup.classes')}</h2>
      <p className="muted">{t('setup.classesHint')}</p>
      <Can permission="assets.manage">
        <form className="inline-form" onSubmit={(e) => { e.preventDefault(); create.mutate(); }} noValidate aria-label={t('setup.newClass')}>
          <div className="row">
            <Field label={t('assets.code')} error={errors.code?.[0]} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} /></Field>
            <Field label={t('assets.name')} error={errors.name?.[0]} required><input value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={200} /></Field>
            <Field label={t('assets.nameAr')} error={errors.nameAr?.[0]}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
            <Field label={t('setup.parentClass')} error={errors.parentId?.[0]}>
              <select value={parentId} onChange={(e) => setParentId(e.target.value)}>
                <option value="">{t('common.notSet')}</option>
                {(classes.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {name(c)}</option>)}
              </select>
            </Field>
          </div>
          <ErrorBanner error={Object.keys(errors).length ? null : create.error} />
          <button type="submit" className="primary" disabled={create.isPending}>{t('common.save')}</button>
        </form>
      </Can>
      <ErrorBanner error={archive.error} />
      <QueryState query={classes}>
        {classes.data && classes.data.length === 0 ? <p className="muted">{t('setup.noClasses')}</p> : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('setup.classes')}</caption>
              <thead><tr><th scope="col">{t('assets.code')}</th><th scope="col">{t('assets.name')}</th><th scope="col">{t('assets.nameAr')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr></thead>
              <tbody>
                {classes.data?.map((c) => (
                  <tr key={c.id}>
                    <td className="code">{c.code}</td><td>{c.name}</td><td lang="ar" dir="rtl">{c.nameAr}</td>
                    <td><Can permission="assets.manage"><button type="button" onClick={() => archive.mutate(c)}>{t('common.archive')}</button></Can></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryState>
    </>
  );
}

const TYPES: CustomFieldType[] = ['Text', 'Number', 'Date', 'Boolean', 'Choice'];

function FieldsSection() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const client = useQueryClient();
  const typeLabel = useEnumLabel('fieldType');
  const targetLabel = useEnumLabel('fieldTarget');
  const classes = useAssetClasses();
  const fields = useQuery({ queryKey: ['custom-fields', 'all'], queryFn: () => api.get<CustomFieldDefinition[]>('/custom-fields') });
  const [target, setTarget] = useState<CustomFieldTarget>('Asset');
  const [key, setKey] = useState('');
  const [labelEn, setLabelEn] = useState('');
  const [labelAr, setLabelAr] = useState('');
  const [dataType, setDataType] = useState<CustomFieldType>('Text');
  const [choices, setChoices] = useState('');
  const [required, setRequired] = useState(false);
  const [classId, setClassId] = useState('');

  const refresh = () => client.invalidateQueries({ queryKey: ['custom-fields'] });
  const create = useMutation({
    mutationFn: () =>
      api.post<CustomFieldDefinition>('/custom-fields', {
        target, key: key.trim(), labelEn: labelEn.trim(), labelAr: labelAr.trim() || null, dataType, isRequired: required, sortOrder: 0,
        choices: dataType === 'Choice' ? choices.split('\n').map((c) => c.trim()).filter(Boolean) : null, assetClassId: target === 'Asset' && classId ? classId : null,
      }),
    onSuccess: async () => {
      setKey(''); setLabelEn(''); setLabelAr(''); setChoices(''); setRequired(false); setClassId('');
      await refresh();
    },
  });
  const archive = useMutation({
    mutationFn: (f: CustomFieldDefinition) => api.post(`/custom-fields/${f.id}/archive`),
    onSuccess: refresh,
  });
  const errors = create.error instanceof ApiError ? create.error.fieldErrors : {};

  return (
    <>
      <h2>{t('setup.fields')}</h2>
      <p className="muted">{t('setup.fieldsHint')}</p>
      <Can permission="customfields.manage">
        <form className="inline-form" onSubmit={(e) => { e.preventDefault(); create.mutate(); }} noValidate aria-label={t('setup.newField')}>
          <div className="row">
            <Field label={t('setup.appliesTo')} error={errors.target?.[0]}>
              <select value={target} onChange={(e) => setTarget(e.target.value as CustomFieldTarget)}>
                {(['Asset', 'Location'] as CustomFieldTarget[]).map((x) => <option key={x} value={x}>{targetLabel(x)}</option>)}
              </select>
            </Field>
            <Field label={t('setup.key')} error={errors.key?.[0]} required hint={t('setup.keyHint')}><input value={key} onChange={(e) => setKey(e.target.value)} maxLength={50} autoComplete="off" /></Field>
            <Field label={t('setup.labelEn')} error={errors.labelEn?.[0]} required><input value={labelEn} onChange={(e) => setLabelEn(e.target.value)} maxLength={200} /></Field>
            <Field label={t('setup.labelAr')} error={errors.labelAr?.[0]}><input value={labelAr} onChange={(e) => setLabelAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
            <Field label={t('setup.type')} error={errors.dataType?.[0]}>
              <select value={dataType} onChange={(e) => setDataType(e.target.value as CustomFieldType)}>
                {TYPES.map((x) => <option key={x} value={x}>{typeLabel(x)}</option>)}
              </select>
            </Field>
            {target === 'Asset' ? (
              <Field label={t('setup.onlyForClass')} error={errors.assetClassId?.[0]}>
                <select value={classId} onChange={(e) => setClassId(e.target.value)}>
                  <option value="">{t('setup.allClasses')}</option>
                  {(classes.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {name(c)}</option>)}
                </select>
              </Field>
            ) : null}
          </div>
          {dataType === 'Choice' ? (
            <Field label={t('setup.choices')} error={errors.choices?.[0]} hint={t('setup.choicesHint')}>
              <textarea rows={3} value={choices} onChange={(e) => setChoices(e.target.value)} />
            </Field>
          ) : null}
          <label className="check"><input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} /> {t('setup.required')}</label>
          <ErrorBanner error={Object.keys(errors).length ? null : create.error} />
          <button type="submit" className="primary" disabled={create.isPending}>{t('common.save')}</button>
        </form>
      </Can>
      <ErrorBanner error={archive.error} />
      <QueryState query={fields}>
        {fields.data && fields.data.length === 0 ? <p className="muted">{t('setup.noFields')}</p> : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('setup.fields')}</caption>
              <thead><tr><th scope="col">{t('setup.appliesTo')}</th><th scope="col">{t('setup.key')}</th><th scope="col">{t('setup.labelEn')}</th><th scope="col">{t('setup.labelAr')}</th><th scope="col">{t('setup.type')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr></thead>
              <tbody>
                {fields.data?.map((f) => (
                  <tr key={f.id}>
                    <td>{targetLabel(f.target)}</td><td className="code">{f.key}</td><td>{f.labelEn}</td><td lang="ar" dir="rtl">{f.labelAr}</td>
                    <td>{typeLabel(f.dataType)} {f.isRequired ? <Badge tone="warn">{t('setup.requiredBadge')}</Badge> : null}</td>
                    <td><Can permission="customfields.manage"><button type="button" onClick={() => archive.mutate(f)}>{t('common.archive')}</button></Can></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryState>
    </>
  );
}
