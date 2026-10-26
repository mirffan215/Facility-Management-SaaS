import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssetClasses, useCustomFields, useLocationTree } from '../api/hooks';
import type { Asset, CustomFieldValues, Criticality, Page } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { breadcrumb, buildTree, flatten } from '../lib/locations';
import { usePrefs } from '../lib/prefs';
import { CustomFieldInputs } from './CustomFieldInputs';
import { ErrorBanner, Field, useEnumLabel } from './ui';

export const CRITICALITIES: Criticality[] = ['Low', 'Medium', 'High', 'Critical'];

interface Props {
  asset?: Asset;
  /** Location to preselect on create (e.g. when arriving from a location). */
  initialLocationId?: string;
  onDone: (saved?: Asset) => void;
}

export function AssetForm({ asset, initialLocationId, onDone }: Props) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name: nameOf } = usePrefs();
  const client = useQueryClient();
  const critLabel = useEnumLabel('criticality');
  const classes = useAssetClasses();
  const tree = useLocationTree();
  const fields = useCustomFields('Asset');

  const [code, setCode] = useState(asset?.code ?? '');
  const [name, setName] = useState(asset?.name ?? '');
  const [nameAr, setNameAr] = useState(asset?.nameAr ?? '');
  const [classId, setClassId] = useState(asset?.assetClassId ?? '');
  const [locationId, setLocationId] = useState(asset?.locationId ?? initialLocationId ?? '');
  const [parentCode, setParentCode] = useState('');
  const [parentId, setParentId] = useState<string | null>(asset?.parentAssetId ?? null);
  const [criticality, setCriticality] = useState<Criticality>(asset?.criticality ?? 'Medium');
  const [manufacturer, setManufacturer] = useState(asset?.manufacturer ?? '');
  const [model, setModel] = useState(asset?.model ?? '');
  const [serial, setSerial] = useState(asset?.serialNumber ?? '');
  const [install, setInstall] = useState(asset?.installDate ?? '');
  const [warranty, setWarranty] = useState(asset?.warrantyExpiry ?? '');
  const [startActive, setStartActive] = useState(false);
  const [custom, setCustom] = useState<CustomFieldValues>(asset?.customFields ?? {});

  const placeOptions = useMemo(() => flatten(buildTree(tree.data ?? [])), [tree.data]);
  const applicable = (fields.data ?? []).filter((d) => d.assetClassId === null || d.assetClassId === classId);

  // Show the current parent's code when editing.
  const parentLookup = useQuery({
    queryKey: ['assets', asset?.parentAssetId],
    queryFn: () => api.get<Asset>(`/assets/${asset!.parentAssetId}`),
    enabled: !!asset?.parentAssetId,
  });
  useEffect(() => {
    if (parentLookup.data) setParentCode(parentLookup.data.code);
  }, [parentLookup.data]);

  const candidates = useQuery({
    queryKey: ['assets', 'parent-candidates', parentCode],
    queryFn: () => api.get<Page<Asset>>('/assets', { q: parentCode, pageSize: 10 }),
    enabled: parentCode.trim().length >= 2,
  });

  const onParentInput = (value: string) => {
    setParentCode(value);
    const exact = candidates.data?.items.find((a) => a.code.toLowerCase() === value.trim().toLowerCase());
    setParentId(value.trim() === '' ? null : (exact?.id ?? null));
  };

  const save = useMutation({
    mutationFn: () => {
      const body = {
        code: code.trim(), name: name.trim(), nameAr: nameAr.trim() || null, assetClassId: classId || null, locationId: locationId || null,
        parentAssetId: parentId, criticality, manufacturer: manufacturer.trim() || null, model: model.trim() || null,
        serialNumber: serial.trim() || null, installDate: install || null, warrantyExpiry: warranty || null, customFields: custom,
      };
      return asset
        ? api.put<Asset>(`/assets/${asset.id}`, { ...body, rowVersion: asset.rowVersion })
        : api.post<Asset>('/assets', { ...body, startActive });
    },
    onSuccess: async (saved) => {
      await client.invalidateQueries({ queryKey: ['assets'] });
      onDone(saved);
    },
  });

  const errors = save.error instanceof ApiError ? save.error.fieldErrors : {};
  const first = (f: string) => errors[f]?.[0];
  const parentTyped = parentCode.trim() !== '' && parentId === null;

  return (
    <form
      className="form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        if (!parentTyped) save.mutate();
      }}
    >
      <h2>{asset ? t('assets.editTitle') : t('assets.newTitle')}</h2>
      <Field label={t('assets.code')} error={first('code')} required hint={t('assets.codeHint')}>
        <input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} autoComplete="off" />
      </Field>
      <Field label={t('assets.name')} error={first('name')} required>
        <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} />
      </Field>
      <Field label={t('assets.nameAr')} error={first('nameAr')}>
        <input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" />
      </Field>
      <Field label={t('assets.class')} error={first('assetClassId')} required>
        <select value={classId} onChange={(e) => setClassId(e.target.value)}>
          <option value="">{t('common.choose')}</option>
          {(classes.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {nameOf(c)}
            </option>
          ))}
        </select>
      </Field>
      <Field label={t('assets.location')} error={first('locationId')} required>
        <select value={locationId} onChange={(e) => setLocationId(e.target.value)}>
          <option value="">{t('common.choose')}</option>
          {placeOptions.map((n) => (
            <option key={n.id} value={n.id}>
              {breadcrumb(tree.data ?? [], n.id, nameOf)}
            </option>
          ))}
        </select>
      </Field>
      <Field
        label={t('assets.partOf')}
        error={first('parentAssetId') ?? (parentTyped ? t('assets.partOfNotFound') : undefined)}
        hint={t('assets.partOfHint')}
      >
        <input value={parentCode} onChange={(e) => onParentInput(e.target.value)} list="parent-candidates" autoComplete="off" />
      </Field>
      <datalist id="parent-candidates">
        {(candidates.data?.items ?? [])
          .filter((a) => a.id !== asset?.id)
          .map((a) => (
            <option key={a.id} value={a.code}>
              {nameOf(a)}
            </option>
          ))}
      </datalist>
      <Field label={t('assets.criticality')} error={first('criticality')}>
        <select value={criticality} onChange={(e) => setCriticality(e.target.value as Criticality)}>
          {CRITICALITIES.map((c) => (
            <option key={c} value={c}>
              {critLabel(c)}
            </option>
          ))}
        </select>
      </Field>
      <div className="row">
        <Field label={t('assets.manufacturer')} error={first('manufacturer')}>
          <input value={manufacturer} onChange={(e) => setManufacturer(e.target.value)} maxLength={200} />
        </Field>
        <Field label={t('assets.model')} error={first('model')}>
          <input value={model} onChange={(e) => setModel(e.target.value)} maxLength={200} />
        </Field>
        <Field label={t('assets.serial')} error={first('serialNumber')}>
          <input value={serial} onChange={(e) => setSerial(e.target.value)} maxLength={100} />
        </Field>
      </div>
      <div className="row">
        <Field label={t('assets.installDate')} error={first('installDate')}>
          <input type="date" value={install} onChange={(e) => setInstall(e.target.value)} />
        </Field>
        <Field label={t('assets.warrantyExpiry')} error={first('warrantyExpiry')}>
          <input type="date" value={warranty} onChange={(e) => setWarranty(e.target.value)} />
        </Field>
      </div>
      <CustomFieldInputs definitions={applicable} values={custom} errors={errors} onChange={setCustom} />
      {!asset ? (
        <label className="check">
          <input type="checkbox" checked={startActive} onChange={(e) => setStartActive(e.target.checked)} /> {t('assets.startActive')}
        </label>
      ) : null}
      <ErrorBanner error={save.error instanceof ApiError && save.error.status === 400 ? null : save.error} />
      {Object.keys(errors).length > 0 ? (
        <p className="banner error" role="alert">
          {t('errors.checkFields')}
        </p>
      ) : null}
      <div className="actions">
        <button type="button" onClick={() => onDone()}>
          {t('common.cancel')}
        </button>
        <button type="submit" className="primary" disabled={save.isPending}>
          {t('common.save')}
        </button>
      </div>
    </form>
  );
}
