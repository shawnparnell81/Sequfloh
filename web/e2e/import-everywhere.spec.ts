import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/**
 * Import file / Import folder on every upload page. Each page gets a made-up folder with a subfolder, a file of a type the page does not take and a
 * hidden file: the accepted files are imported one by one, the others are skipped with the reason, and the summary counts them.
 */
const shots = process.env.E2E_SHOTS;
const jpg = fs.readFileSync(new URL('./example-evidence.jpg', import.meta.url));

function tree(stamp: string, ext: string, body: (n: string) => Buffer | string) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sqf-import-'));
  const top = path.join(root, `Example batch ${stamp}`);
  fs.mkdirSync(path.join(top, 'Line 2', 'Night shift'), { recursive: true });
  const names = [`first-${stamp}${ext}`, path.join('Line 2', `second-${stamp}${ext}`), path.join('Line 2', 'Night shift', `third-${stamp}${ext}`)];
  for (const n of names) fs.writeFileSync(path.join(top, n), body(n));
  fs.writeFileSync(path.join(top, `tool-${stamp}.exe`), 'MZ');
  fs.writeFileSync(path.join(top, '.hidden-notes.txt'), 'x');
  return { top, names };
}

async function runImport(page: Page, surface: string, dir: string, shot: string, before?: () => Promise<void>) {
  const box = view(page).locator(`[data-upload-surface="${surface}"]`);
  await expect(box.getByRole('button', { name: 'Import folder' })).toBeVisible();
  await box.getByTestId(`${surface}-folder-input`).setInputFiles(dir);
  const dlg = page.getByTestId('bulk-import');
  await expect(dlg.getByTestId('bulk-row')).toHaveCount(5);
  await expect(dlg).toContainText('file type not accepted here (.exe)');
  await expect(dlg).toContainText('system or hidden file');
  await expect(dlg.getByTestId('bulk-summary')).toContainText('To import 3');
  if (before) await before();
  if (shots) await settledShot(page, path.join(shots, `${shot}-1-picked.png`));
  await dlg.getByTestId('bulk-import-start').click();
  await expect(dlg.getByTestId('bulk-done')).toContainText('imported, 2 skipped, 0 failed');
  await expect(dlg.getByTestId('bulk-done')).toContainText('Finished: 3 ');
  if (shots) await settledShot(page, path.join(shots, `${shot}-2-summary.png`));
  await dlg.getByRole('button', { name: 'Close' }).click();
}

test('Import folder on every upload page: subfolders included, other types skipped, one record or attachment per file', async ({ page }) => {
  test.setTimeout(240_000);
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;

  // Imported Files: each spreadsheet becomes its own imported file
  await page.goto('/sequfloh/imported-files');
  const csv = tree(stamp, '.csv', (n) => `Unit,Reading\nU-1,${n.length}\n`);
  await runImport(page, 'imported-files', csv.top, '01-imported-files');
  for (const n of csv.names) await expect(view(page)).toContainText(path.basename(n, '.csv'));

  // Work Instructions list: each file becomes its own draft work instruction with the file attached
  await page.goto('/sequfloh/instructions/repair');
  const wiFiles = tree(stamp, '.txt', (n) => `Made-up repair steps for ${n}`);
  await runImport(page, 'work-instructions', wiFiles.top, '02-work-instructions', async () => {
    await expect(page.getByTestId('wi-import-type')).toHaveValue('RepairWork');
  });
  const list = await ok(rq.get(`/api/sequfloh/instructions?folder=all&q=${stamp}&pageSize=50`));
  expect(list.items.map((x: { title: string }) => x.title).sort()).toEqual([`first-${stamp}`, `second-${stamp}`, `third-${stamp}`]);
  const made = list.items[0].id as string;
  const files = await ok(rq.get(`/api/records/SequflohWorkInstruction/${made}/files`));
  expect(files.length).toBe(1);

  // WI files panel (Layout tab of a draft)
  await page.goto(`/sequfloh/instruction/${made}`);
  await view(page).getByRole('tab', { name: 'Layout' }).click();
  const wf = tree(stamp + 'w', '.txt', () => 'Made-up picture notes');
  await runImport(page, 'work-instruction-files', wf.top, '03-work-instruction-files');
  await expect(view(page)).toContainText(`third-${stamp}w.txt`);

  // inspection request attachments
  const ir = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, siteId: site, partNumber: `IMP-${stamp}`, priority: 'Normal', requestedDate: '2026-11-20' } }))).id as string;
  await page.goto(`/sequfloh/inspection-request/${ir}`);
  const irf = tree(stamp + 'i', '.pdf', () => '%PDF-1.4\n% made-up\n');
  await runImport(page, 'inspection-request-files', irf.top, '04-inspection-request-files');
  await expect(view(page)).toContainText(`second-${stamp}i.pdf`);

  // work order: tester files, photos and attachments go to the work order only
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Import WO instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-I${stamp}` } }))).id as string;
  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByRole('tab', { name: 'Job' }).click();
  const tf = tree(stamp + 't', '.csv', () => 'Unit,Outcome\n1,PASS\n2,PASS\n');
  await runImport(page, 'work-order-tester-files', tf.top, '05-work-order-tester-files');
  await expect(view(page)).toContainText(`first-${stamp}t.csv`);
  await view(page).getByRole('tab', { name: 'Findings' }).click();
  const ph = tree(stamp + 'p', '.jpg', () => jpg);
  await runImport(page, 'work-order-evidence', ph.top, '06-work-order-photos');
  const at = tree(stamp + 'a', '.txt', () => 'Made-up supplier note');
  await runImport(page, 'work-order-attachments', at.top, '07-work-order-attachments');
  await expect(view(page)).toContainText(`third-${stamp}a.txt`);
  // never cross-created: no work instruction was made from the work order files
  const cross = await ok(rq.get(`/api/sequfloh/instructions?q=${stamp}t&pageSize=50`));
  expect(cross.items.length).toBe(0);

  // Parts Drawings: the shared buttons open the drawings import with the picked folder
  await page.goto('/sequfloh/drawings');
  const dr = tree(stamp + 'd', '.pdf', () => '%PDF-1.4\n% made-up drawing\n');
  await view(page).locator('[data-upload-surface="parts-drawings"]').getByTestId('parts-drawings-folder-input').setInputFiles(dr.top);
  const dlg = page.getByRole('dialog');
  await expect(dlg).toContainText('Skipped (not PDF or Word)1');
  await expect(dlg.getByRole('button', { name: 'Import 3 drawings' })).toBeEnabled();
  if (shots) await settledShot(page, path.join(shots, '08-parts-drawings-picked.png'));
});
