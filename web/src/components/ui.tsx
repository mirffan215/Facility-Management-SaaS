import { cloneElement, isValidElement, useEffect, useId, useRef, useState, type ReactElement, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { usePermissions } from '../api/hooks';

/** Renders children only when the signed-in user holds the permission. The server enforces it either way. */
export function Can({ permission, children }: { permission: string; children: ReactNode }) {
  const { can } = usePermissions();
  return can(permission) ? <>{children}</> : null;
}

interface FieldProps {
  label: string;
  error?: string;
  hint?: string;
  required?: boolean;
  children: ReactElement<{ id?: string; 'aria-invalid'?: boolean; 'aria-describedby'?: string; 'aria-required'?: boolean }>;
}

/** A labelled input with its error and hint wired up for screen readers. */
export function Field({ label, error, hint, required, children }: FieldProps) {
  const id = useId();
  const describedBy = [error ? `${id}-error` : '', hint ? `${id}-hint` : ''].filter(Boolean).join(' ') || undefined;
  return (
    <div className="field">
      <label htmlFor={id} className={required ? 'required' : undefined}>
        {label}
      </label>
      {isValidElement(children)
        ? cloneElement(children, { id, 'aria-invalid': error ? true : undefined, 'aria-describedby': describedBy, 'aria-required': required ? true : undefined })
        : children}
      {hint ? <small id={`${id}-hint`}>{hint}</small> : null}
      {error ? (
        <small id={`${id}-error`} className="error" role="alert">
          {error}
        </small>
      ) : null}
    </div>
  );
}

/** A readable message for a failed call. Field-level errors are shown by the fields; this is for everything else. */
export function errorText(error: unknown, t: (key: string) => string): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 0:
        return t('errors.network');
      case 403:
        return t('errors.forbidden');
      case 404:
        return t('errors.notFound');
      case 409:
        return error.title || t('errors.conflict');
      case 413:
        return t('errors.tooLarge');
      case 415:
        return error.title || t('errors.unsupportedType');
      default:
        return error.status === 400 && Object.keys(error.fieldErrors).length > 0 ? t('errors.checkFields') : error.title || t('errors.generic');
    }
  }

  return t('errors.generic');
}

export function ErrorBanner({ error }: { error: unknown }) {
  const { t } = useTranslation();
  if (!error) return null;
  return (
    <div className="banner error" role="alert">
      {errorText(error, t)}
    </div>
  );
}

export function Loading() {
  const { t } = useTranslation();
  return (
    <p className="muted" role="status">
      {t('common.loading')}
    </p>
  );
}

export function QueryState({ query, children }: { query: { isLoading: boolean; error: unknown }; children: ReactNode }) {
  if (query.isLoading) return <Loading />;
  if (query.error) return <ErrorBanner error={query.error} />;
  return <>{children}</>;
}

export function Pager({ page, pageSize, total, onChange }: { page: number; pageSize: number; total: number; onChange: (page: number) => void }) {
  const { t } = useTranslation();
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (pages <= 1) return null;
  return (
    <nav className="pager" aria-label={t('common.pagination')}>
      <button type="button" disabled={page <= 1} onClick={() => onChange(page - 1)}>
        {t('common.previous')}
      </button>
      <span>{t('common.pageOf', { page, pages })}</span>
      <button type="button" disabled={page >= pages} onClick={() => onChange(page + 1)}>
        {t('common.next')}
      </button>
    </nav>
  );
}

interface ReasonDialogProps {
  title: string;
  label: string;
  confirmLabel: string;
  /** Whether an empty reason is allowed. */
  optional?: boolean;
  busy?: boolean;
  error?: unknown;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}

/** A modal that asks for a reason before a consequential action. Escape cancels; focus starts in the box. */
export function ReasonDialog({ title, label, confirmLabel, optional, busy, error, onConfirm, onCancel }: ReasonDialogProps) {
  const { t } = useTranslation();
  const [reason, setReason] = useState('');
  const input = useRef<HTMLTextAreaElement>(null);
  const titleId = useId();

  useEffect(() => input.current?.focus(), []);

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onCancel()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <h2 id={titleId}>{title}</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            onConfirm(reason.trim());
          }}
        >
          <Field label={label} required={!optional}>
            <textarea ref={input} value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={500} />
          </Field>
          <ErrorBanner error={error} />
          <div className="actions">
            <button type="button" onClick={onCancel}>
              {t('common.cancel')}
            </button>
            <button type="submit" className="primary" disabled={busy || (!optional && reason.trim().length === 0)}>
              {confirmLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

/** Localised label for an enum value, falling back to the raw value. */
export function useEnumLabel(group: string) {
  const { t } = useTranslation();
  return (value: string | null | undefined) => (value ? t(`${group}.${value}`, { defaultValue: value }) : '');
}

export function Badge({ tone, children }: { tone: 'ok' | 'warn' | 'bad' | 'neutral'; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>;
}
