import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc32 = (b: Buffer) => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };

/** A minimal made-up .docx: an uncompressed ZIP with the content types and word/document.xml. */
function docx(text: string) {
  const entries: [string, string][] = [
    ['[Content_Types].xml', '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'],
    ['word/document.xml', `<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>${text}</w:t></w:r></w:p></w:body></w:document>`]];
  const local: Buffer[] = [], central: Buffer[] = [];
  let offset = 0;
  for (const [name, body] of entries) {
    const n = Buffer.from(name), d = Buffer.from(body), crc = crc32(d);
    const h = Buffer.alloc(30); h.writeUInt32LE(0x04034b50, 0); h.writeUInt16LE(20, 4); h.writeUInt32LE(crc, 14); h.writeUInt32LE(d.length, 18); h.writeUInt32LE(d.length, 22); h.writeUInt16LE(n.length, 26);
    const c = Buffer.alloc(46); c.writeUInt32LE(0x02014b50, 0); c.writeUInt16LE(20, 4); c.writeUInt16LE(20, 6); c.writeUInt32LE(crc, 16); c.writeUInt32LE(d.length, 20); c.writeUInt32LE(d.length, 24); c.writeUInt16LE(n.length, 28); c.writeUInt32LE(offset, 42);
    local.push(h, n, d); central.push(c, n); offset += 30 + n.length + d.length;
  }
  const cd = Buffer.concat(central), e = Buffer.alloc(22);
  e.writeUInt32LE(0x06054b50, 0); e.writeUInt16LE(entries.length, 8); e.writeUInt16LE(entries.length, 10); e.writeUInt32LE(cd.length, 12); e.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, cd, e]);
}
/** A made-up legacy .doc: the OLE2 signature and filler. */
const doc = () => Buffer.concat([Buffer.from([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1]), Buffer.alloc(504)]);
const pdf = () => Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n');

test('word drawings: upload a .docx into a folder, see the Word tag, and import a folder with Word files', async ({ page }) => {
  const shots = process.env.E2E_WD_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'New folder' }).click();
  await page.getByLabel('Folder name').fill(`Word notes ${k}`);
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(v.getByText('This folder is empty.')).toBeVisible();

  // single upload of a Word file
  await v.getByRole('button', { name: 'Upload drawing' }).click();
  await expect(page.getByTestId('drawing-file')).toHaveAttribute('accept', /\.docx,\.doc/);
  await expect(page.getByText(/Word \(DOCX, DOC\) files up to 50 MB/)).toBeVisible();
  await page.getByTestId('drawing-file').setInputFiles({ name: 'assembly-note.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', buffer: docx('Made-up assembly note') });
  await expect(page.getByLabel('Title')).toHaveValue('assembly-note');
  await shot('01-upload-word');
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  await expect(v.getByTestId('current-rev')).toHaveText('A');
  await expect(v.getByTestId('word-label').first()).toBeVisible();
  // the drawing page shows when it was uploaded but not who uploaded it
  const me = (await page.getByTestId('user-menu').innerText()).trim();
  expect(me.length).toBeGreaterThan(0);
  await expect(v.getByRole('row').filter({ hasText: 'assembly-note.docx' })).not.toContainText(me);
  await expect(v.getByRole('table')).not.toContainText(me);
  await shot('02-word-drawing');

  // a folder import with a PDF, a .docx, a .doc and a text file
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'word-import-'));
  const top = path.join(root, `Word import ${k}`);
  fs.mkdirSync(path.join(top, 'Notes'), { recursive: true });
  fs.writeFileSync(path.join(top, 'Notes', 'Cover.pdf'), pdf());
  fs.writeFileSync(path.join(top, 'Notes', 'Fitting instructions.docx'), docx('Made-up fitting instructions'));
  fs.writeFileSync(path.join(top, 'Notes', 'Old spec.doc'), doc());
  fs.writeFileSync(path.join(top, 'readme.txt'), 'made-up notes');

  await v.getByRole('link', { name: `Word notes ${k}`, exact: true }).click();
  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  const preview = dlg.getByTestId('import-preview');
  await expect(preview).toContainText('PDF files1');
  await expect(preview).toContainText('Word files2');
  await expect(preview).toContainText('To import3');
  await expect(dlg.getByText('not a drawing type and will be skipped')).toBeVisible();
  await shot('03-import-preview');
  await dlg.getByRole('button', { name: 'Import 3 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 3 of 3 files');
  await dlg.getByRole('button', { name: 'Close' }).click();
  await v.getByRole('button', { name: `Word import ${k}` }).click();
  await v.getByRole('button', { name: 'Notes', exact: true }).click();
  await expect(v.getByTestId('word-label')).toHaveCount(2);
  await shot('04-imported-word-files');
  fs.rmSync(root, { recursive: true, force: true });
});

test('word drawings: a top-level folder import takes PDF and Word files and skips the rest', async ({ page }) => {
  const shots = process.env.E2E_WD_SHOTS;
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'word-top-'));
  const top = path.join(root, `Example word top ${k}`);
  fs.mkdirSync(path.join(top, `Line W ${k}`), { recursive: true });
  fs.writeFileSync(path.join(top, `Line W ${k}`, 'Sheet.pdf'), pdf());
  fs.writeFileSync(path.join(top, `Line W ${k}`, 'Work instruction.docx'), docx('Made-up work instruction'));
  fs.writeFileSync(path.join(top, `Line W ${k}`, 'Outline.dxf'), '0\nEOF\n');
  fs.writeFileSync(path.join(top, 'readme.txt'), 'made-up notes');

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByText('Only PDF and Word (DOCX, DOC) files are imported')).toBeVisible();
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  const preview = dlg.getByTestId('import-preview');
  await expect(preview).toContainText('PDF files1');
  await expect(preview).toContainText('Word files1');
  await expect(preview).toContainText('Skipped (not PDF or Word)2');
  await expect(preview).toContainText('To import2');
  if (shots) await settledShot(page, path.join(shots, '05-top-level-import-preview.png'));
  await dlg.getByRole('button', { name: 'Import 2 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 2 of 2 files');
  await dlg.getByRole('button', { name: 'Close' }).click();
  await v.getByRole('button', { name: `Line W ${k}` }).click();
  await expect(v.getByTestId('word-label')).toHaveCount(1);
  if (shots) await settledShot(page, path.join(shots, '06-top-level-imported.png'));
  fs.rmSync(root, { recursive: true, force: true });
});
