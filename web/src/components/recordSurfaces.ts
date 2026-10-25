/**
 * Every record page with the shared action bar (RecordActions) and the standard actions it must offer to an administrator. e2e/record-actions.spec.ts
 * opens each one and fails when an action is missing, so a page cannot quietly lose Save, Edit, Cancel, Delete, Print, Import or Full screen.
 * FULLSCREEN_VIEWERS lists the document viewers outside record pages that must offer Full screen too.
 */
export type RecordAction = 'edit' | 'save' | 'cancel' | 'print' | 'import' | 'delete' | 'fullscreen';
export const RECORD_SURFACES = {
  'work-instruction': { label: 'Work instruction (draft)', actions: ['save', 'cancel', 'print', 'delete', 'fullscreen'] },
  'work-order': { label: 'Work order', actions: ['save', 'cancel', 'print', 'delete', 'fullscreen'] },
  'chargeback-report': { label: 'Chargeback report', actions: ['edit', 'print', 'delete', 'fullscreen'] },
  'chargeback-report-edit': { label: 'Chargeback report (editing)', actions: ['save', 'cancel', 'fullscreen'] },
  'drawing': { label: 'Drawing', actions: ['edit', 'print', 'import', 'delete', 'fullscreen'] },
  'drawing-folder': { label: 'Drawing folder', actions: ['edit', 'import', 'delete', 'fullscreen'] },
  'completed-forms': { label: 'Completed Validation/FAI (a product family tab)', actions: ['print', 'import', 'fullscreen'] },
  'completed-form': { label: 'Completed form', actions: ['edit', 'print', 'import', 'delete', 'fullscreen'] },
  'imported-file': { label: 'Imported file / template', actions: ['print', 'import', 'delete', 'fullscreen'] },
  'inspection-request': { label: 'Inspection request', actions: ['edit', 'delete', 'fullscreen'] },
  'part': { label: 'Part', actions: ['edit', 'delete', 'fullscreen'] },
  'parts-list': { label: 'Parts list (draft)', actions: ['edit', 'delete', 'fullscreen'] },
} satisfies Record<string, { label: string; actions: RecordAction[] }>;
export type RecordSurface = keyof typeof RECORD_SURFACES;

/** Actions that live in the More menu rather than in the row. */
export const MENU_ACTIONS: readonly RecordAction[] = ['delete'];

export const FULLSCREEN_VIEWERS = ['drawing-viewer'] as const;
