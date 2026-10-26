import { useTranslation } from 'react-i18next';
import type { CustomFieldDefinition, CustomFieldValues } from '../api/types';
import { Field } from './ui';

interface Props {
  definitions: CustomFieldDefinition[];
  values: CustomFieldValues;
  errors?: Record<string, string[]>;
  onChange: (values: CustomFieldValues) => void;
  disabled?: boolean;
}

/** Inputs for the tenant's custom fields, labelled in the viewer's language. Empty inputs are left out of the payload. */
export function CustomFieldInputs({ definitions, values, errors, onChange, disabled }: Props) {
  const { i18n, t } = useTranslation();
  if (definitions.length === 0) return null;
  const arabic = i18n.language.startsWith('ar');

  const set = (key: string, value: string | number | boolean | undefined) => {
    const next = { ...values };
    if (value === undefined || value === '') delete next[key];
    else next[key] = value;
    onChange(next);
  };

  return (
    <fieldset className="custom-fields" disabled={disabled}>
      <legend>{t('customFields.title')}</legend>
      {definitions.map((d) => {
        const label = arabic && d.labelAr ? d.labelAr : d.labelEn;
        const error = errors?.[`customFields.${d.key}`]?.[0];
        const value = values[d.key];
        switch (d.dataType) {
          case 'Number':
            return (
              <Field key={d.key} label={label} error={error} required={d.isRequired}>
                <input type="number" step="any" value={value === undefined ? '' : String(value)} onChange={(e) => set(d.key, e.target.value === '' ? undefined : Number(e.target.value))} />
              </Field>
            );
          case 'Date':
            return (
              <Field key={d.key} label={label} error={error} required={d.isRequired}>
                <input type="date" value={typeof value === 'string' ? value : ''} onChange={(e) => set(d.key, e.target.value)} />
              </Field>
            );
          case 'Boolean':
            return (
              <Field key={d.key} label={label} error={error} required={d.isRequired}>
                <select value={value === undefined ? '' : String(value)} onChange={(e) => set(d.key, e.target.value === '' ? undefined : e.target.value === 'true')}>
                  <option value="">{t('common.notSet')}</option>
                  <option value="true">{t('common.yes')}</option>
                  <option value="false">{t('common.no')}</option>
                </select>
              </Field>
            );
          case 'Choice':
            return (
              <Field key={d.key} label={label} error={error} required={d.isRequired}>
                <select value={typeof value === 'string' ? value : ''} onChange={(e) => set(d.key, e.target.value)}>
                  <option value="">{t('common.notSet')}</option>
                  {d.choices.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </select>
              </Field>
            );
          default:
            return (
              <Field key={d.key} label={label} error={error} required={d.isRequired}>
                <input type="text" maxLength={1000} value={typeof value === 'string' ? value : ''} onChange={(e) => set(d.key, e.target.value)} />
              </Field>
            );
        }
      })}
    </fieldset>
  );
}
