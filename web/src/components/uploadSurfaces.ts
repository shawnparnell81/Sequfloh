/**
 * Every place in the app that takes uploaded files. Each one shows the shared Import file and Import folder buttons (ImportButtons), and the
 * regression test (e2e/upload-surfaces.spec.ts) opens each surface and fails if either button is missing, or if a file input appears anywhere
 * that is not one of these surfaces or one of the listed single-file replacements.
 */
export const UPLOAD_SURFACES = {
  'imported-files': 'Imported Files (blank templates and forms)',
  'completed-forms': 'Completed Validation / FAI (completed forms into the product family tab)',
  'development-files': 'Engineering, Development (files into the product line tab)',
  'parts-drawings': 'Drawings',
  'work-instructions': 'Work Instructions list',
  'work-instruction-files': 'Work instruction files',
  'inspection-request-files': 'Inspection request attachments',
  'work-order-tester-files': 'Work order tester files',
  'work-order-evidence': 'Work order photos and evidence',
  'work-order-attachments': 'Work order attachments',
} as const;
export type UploadSurface = keyof typeof UPLOAD_SURFACES;

/** File inputs that replace ONE existing file, read one spreadsheet into a mapping wizard, or sit inside a dialog the shared buttons open. */
export const SINGLE_FILE_INPUTS: Record<string, string> = {
  'drawing-file': 'Upload drawing: one file for one new drawing, with its title and parts (folders and many files go through Import file / Import folder on the same page)',
  'revision-file': 'New revision of one drawing',
  'upload-version-input': 'New version of one imported file',
  'import-files-input': 'Inside the Import drawings dialog (the same dialog the shared Import file / Import folder buttons open)',
  'import-folder-input': 'Inside the Import drawings dialog (the same dialog the shared Import file / Import folder buttons open)',
  'import-file': 'Parts import: one spreadsheet read into the column mapping wizard',
};
