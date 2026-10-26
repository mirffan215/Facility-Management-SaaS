import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import '../i18n';
import { ApiError } from '../api/client';
import { CustomFieldInputs } from './CustomFieldInputs';
import { Field, Pager, ReasonDialog, errorText } from './ui';
import type { CustomFieldDefinition } from '../api/types';

describe('Field', () => {
  it('links the label, hint and error to the input', () => {
    render(
      <Field label="Code" hint="Letters only" error="Code is required" required>
        <input />
      </Field>,
    );
    const input = screen.getByLabelText('Code');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAttribute('aria-required', 'true');
    expect(input).toHaveAccessibleDescription('Code is required Letters only');
    expect(screen.getByRole('alert')).toHaveTextContent('Code is required');
  });

  it('adds no error markers when valid', () => {
    render(<Field label="Name"><input /></Field>);
    expect(screen.getByLabelText('Name')).not.toHaveAttribute('aria-invalid');
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('ReasonDialog', () => {
  it('needs a reason before it confirms, and sends it trimmed', async () => {
    const onConfirm = vi.fn();
    render(<ReasonDialog title="Void it?" label="Reason" confirmLabel="Void" onConfirm={onConfirm} onCancel={() => {}} />);
    const confirm = screen.getByRole('button', { name: 'Void' });
    expect(confirm).toBeDisabled();
    expect(screen.getByLabelText('Reason')).toHaveFocus();

    await userEvent.type(screen.getByLabelText('Reason'), '  typo  ');
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);
    expect(onConfirm).toHaveBeenCalledWith('typo');
  });

  it('allows an empty reason when optional and closes on Escape', async () => {
    const onCancel = vi.fn();
    render(<ReasonDialog title="Archive?" label="Reason" confirmLabel="Archive" optional onConfirm={() => {}} onCancel={onCancel} />);
    expect(screen.getByRole('button', { name: 'Archive' })).toBeEnabled();
    await userEvent.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toHaveAttribute('aria-modal', 'true');
  });
});

describe('Pager', () => {
  it('shows nothing for one page and moves between pages', async () => {
    const onChange = vi.fn();
    const { rerender } = render(<Pager page={1} pageSize={25} total={10} onChange={onChange} />);
    expect(screen.queryByRole('navigation')).toBeNull();

    rerender(<Pager page={2} pageSize={25} total={60} onChange={onChange} />);
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await userEvent.click(screen.getByRole('button', { name: 'Previous' }));
    expect(onChange.mock.calls).toEqual([[3], [1]]);

    rerender(<Pager page={3} pageSize={25} total={60} onChange={onChange} />);
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});

describe('errorText', () => {
  const t = (key: string) => key;
  it('maps statuses to translated messages', () => {
    expect(errorText(new ApiError(0, 'network'), t)).toBe('errors.network');
    expect(errorText(new ApiError(403, 'Forbidden'), t)).toBe('errors.forbidden');
    expect(errorText(new ApiError(404, 'x'), t)).toBe('errors.notFound');
    expect(errorText(new ApiError(413, 'x'), t)).toBe('errors.tooLarge');
    expect(errorText(new ApiError(409, 'The code is already used.'), t)).toBe('The code is already used.');
    expect(errorText(new ApiError(400, 'Validation failed', { code: ['bad'] }), t)).toBe('errors.checkFields');
    expect(errorText(new ApiError(500, ''), t)).toBe('errors.generic');
    expect(errorText(new Error('boom'), t)).toBe('errors.generic');
  });
});

describe('CustomFieldInputs', () => {
  const def = (over: Partial<CustomFieldDefinition>): CustomFieldDefinition => ({
    id: over.key ?? 'k', target: 'Asset', key: 'k', labelEn: 'Label', labelAr: 'تسمية', dataType: 'Text', isRequired: false, choices: [], assetClassId: null, sortOrder: 0, isArchived: false, ...over,
  });

  it('renders nothing without definitions', () => {
    const { container } = render(<CustomFieldInputs definitions={[]} values={{}} onChange={() => {}} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('edits each type, leaves empty values out and shows server errors per field', async () => {
    const onChange = vi.fn();
    render(
      <CustomFieldInputs
        definitions={[
          def({ key: 'note', labelEn: 'Note' }),
          def({ key: 'tons', labelEn: 'Tons', dataType: 'Number', isRequired: true }),
          def({ key: 'fuel', labelEn: 'Fuel', dataType: 'Choice', choices: ['gas', 'electric'] }),
          def({ key: 'crit', labelEn: 'Critical', dataType: 'Boolean' }),
        ]}
        values={{ note: 'hi', tons: 450 }}
        errors={{ 'customFields.tons': ['Must be a number.'] }}
        onChange={onChange}
      />,
    );

    expect(screen.getByLabelText('Tons')).toHaveValue(450);
    expect(screen.getByLabelText('Tons')).toHaveAccessibleDescription('Must be a number.');

    await userEvent.selectOptions(screen.getByLabelText('Fuel'), 'electric');
    expect(onChange).toHaveBeenLastCalledWith({ note: 'hi', tons: 450, fuel: 'electric' });

    await userEvent.selectOptions(screen.getByLabelText('Critical'), 'true');
    expect(onChange).toHaveBeenLastCalledWith({ note: 'hi', tons: 450, crit: true });

    await userEvent.clear(screen.getByLabelText('Note'));
    expect(onChange).toHaveBeenLastCalledWith({ tons: 450 });
  });
});
