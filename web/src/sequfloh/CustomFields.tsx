import { useEffect } from 'react';
import { MenuItem, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { get, put } from '../api';
import { S } from './common';

export interface CustomChoice { id: string; label: string; isActive: boolean; isDefault: boolean }
export interface CustomField {
  id: string; name: string; required: boolean; showInList: boolean; includeInCsv: boolean; filterable: boolean;
  valueId?: string | null; valueLabel?: string | null; values: CustomChoice[];
}
export interface PartCustom { dropdownId: string; name: string; label: string }

const recordType: Record<string, string> = {
  parts: 'Part', drawings: 'Drawing', 'inspection-requests': 'InspectionRequest', 'work-instructions': 'WorkInstruction',
  projects: 'Project', chargebacks: 'Chargeback', ncr: 'Tracking', 'fai-validation': 'Tracking',
};

export function recordTypeFor(screen: string) {
  return recordType[screen] ?? (screen.startsWith('work-orders:') ? 'Tracking' : 'Record');
}

export function useCustomFields(screen: string, recordId?: string) {
  const type = recordTypeFor(screen);
  return useQuery({
    queryKey: ['custom-fields', screen, recordId ?? ''],
    queryFn: () => get<CustomField[]>(`${S}/custom-fields?screen=${encodeURIComponent(screen)}${recordId ? `&recordType=${type}&recordId=${recordId}` : ''}`),
  });
}

/** Dropdowns an administrator placed on this screen. Choosing a value is saved with the record. */
export function CustomFieldInputs({ screen, recordId, value, onChange }: {
  screen: string; recordId?: string; value: Record<string, string>; onChange: (v: Record<string, string>) => void;
}) {
  const q = useCustomFields(screen, recordId);
  useEffect(() => {
    const fields = q.data ?? [];
    if (fields.length === 0) return;
    const next = { ...value };
    let changed = false;
    for (const f of fields) {
      if (next[f.id] !== undefined) continue;
      const preset = f.valueId || f.values.find((v) => v.isDefault)?.id || '';
      next[f.id] = preset;
      changed = true;
    }
    if (changed) onChange(next);
    // fill defaults once when the field list arrives
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q.data]);
  if (!q.data?.length) return null;
  return <>
    {q.data.map((f) => <TextField key={f.id} select label={f.name} required={f.required} value={value[f.id] ?? ''} onChange={(e) => onChange({ ...value, [f.id]: e.target.value })}
      inputProps={{ 'data-testid': `custom-field-${f.name}` }} helperText={f.required ? 'Required' : 'Optional'}>
      {!f.required && <MenuItem value="">None</MenuItem>}
      {f.values.filter((v) => v.isActive || v.id === value[f.id]).map((v) => <MenuItem key={v.id} value={v.id}>{v.isActive ? v.label : `${v.label} (inactive)`}</MenuItem>)}
    </TextField>)}
  </>;
}

export function CustomFieldDetails({ screen, recordId }: { screen: string; recordId: string }) {
  const q = useCustomFields(screen, recordId);
  if (!q.data?.length) return null;
  return <>
    {q.data.map((f) => <Typography key={f.id} variant="body2" data-testid={`custom-value-${f.name}`}><b>{f.name}:</b> {f.valueLabel || '—'}</Typography>)}
  </>;
}

export async function saveCustomFields(screen: string, recordId: string, value: Record<string, string>, label?: string) {
  const fields = await get<CustomField[]>(`${S}/custom-fields?screen=${encodeURIComponent(screen)}`);
  const chosen = fields.filter((f) => value[f.id] !== undefined);
  if (chosen.length === 0) return;
  await put(`${S}/custom-fields/${recordTypeFor(screen)}/${recordId}`, {
    label, values: chosen.map((f) => ({ dropdownId: f.id, valueId: value[f.id] || null })),
  });
}

export function missingRequired(fields: CustomField[] | undefined, value: Record<string, string>) {
  return (fields ?? []).find((f) => f.required && !value[f.id]);
}
