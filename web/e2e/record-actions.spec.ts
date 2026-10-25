import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { FULLSCREEN_VIEWERS, MENU_ACTIONS, RECORD_SURFACES, type RecordSurface } from '../src/components/recordSurfaces';
import { more, settledShot, signIn, view } from './support';

/**
 * Every record page uses the one shared action bar and offers the standard actions listed in recordSurfaces.ts. Also checks that Delete names the
 * item, Cancel asks before throwing changes away and leaving a page with unsaved changes asks first. Made-up records only.
 */
test('standard record actions on every record page', async ({ page }) => {
  test.skip(!process.env.E2E_XLSX, 'set E2E_XLSX');
  test.setTimeout(180_000);
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;

  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Actions instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  const draft = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Actions draft ${stamp}`, siteId: site } }))).id as string;
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-RA${stamp}` } }))).id as string;
  const cb = (await ok(rq.post('/api/sequfloh/chargebacks', { data: { workOrderNumber: `WO-RA${stamp}`, trackingRecordId: wo, supplier: 'Example Supplier Co' } }))).id as string;
  const ir = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, siteId: site, partNumber: `RA-${stamp}`, priority: 'Normal', requestedDate: '2026-11-20' } }))).id as string;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `RA-${stamp}`, name: 'Test spacer', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false } } }))).id as string;
  const list = (await ok(rq.post('/api/sequfloh/parts-lists', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, title: `Actions list ${stamp}`, parentPartId: part, description: null, listType: 'Standard' } }))).id as string;
  const folder = (await ok(rq.post('/api/drawings/folders', { data: { name: `Actions ${stamp}`, siteId: site } }))).id as string;
  const drawing = (await ok(rq.post(`/api/drawings/folders/${folder}/drawings`, { multipart: {
    file: { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n') }, title: 'Test bracket', drawingNumber: `DWG-RA${stamp}`, revision: 'A' } }))).id as string;
  const file = (await ok(rq.post('/api/imported-files', { multipart: {
    file: { name: `actions-${stamp}.xlsx`, mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: fs.readFileSync(path.resolve(process.env.E2E_XLSX!)) }, title: `Actions file ${stamp}` } }))).id as string;
  const fam = (await (await rq.get('/api/sequfloh/validation-forms')).json()).families[0].id as string;
  const done = (await ok(rq.post('/api/imported-files', { multipart: {
    file: { name: `done-${stamp}.xlsx`, mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: fs.readFileSync(path.resolve(process.env.E2E_XLSX!)) }, title: `Completed ${stamp}`, completedForm: 'true', familyId: fam } }))).id as string;

  const visits: Record<RecordSurface, string> = {
    'work-instruction': `/sequfloh/instruction/${draft}`,
    'work-order': `/sequfloh/record/${wo}`,
    'chargeback-report': `/sequfloh/chargebacks/${cb}/view`,
    'chargeback-report-edit': `/sequfloh/chargebacks/${cb}`,
    'drawing': `/sequfloh/drawings/d/${drawing}`,
    'drawing-folder': `/sequfloh/drawings?folder=${folder}`,
    'imported-file': `/sequfloh/imported-files/${file}`,
    'completed-form': `/sequfloh/imported-files/${done}`,
    'completed-forms': `/sequfloh/completed-forms?family=${fam}`,
    'inspection-request': `/sequfloh/inspection-request/${ir}`,
    'part': `/sequfloh/parts/${part}`,
    'parts-list': `/sequfloh/parts-lists/${list}`,
  };
  expect(Object.keys(visits).sort()).toEqual(Object.keys(RECORD_SURFACES).sort());
  for (const [s, url] of Object.entries(visits) as [RecordSurface, string][]) {
    await page.goto(url);
    const bar = view(page).locator(`[data-record-actions="${s}"]`);
    await expect(bar, s).toBeVisible();
    // the bar sits in the page header or the record header, never in an ad-hoc row
    await expect(view(page).locator(`[data-page-actions] [data-record-actions="${s}"]`), `${s} in a header`).toHaveCount(1);
    // destructive actions are never loose buttons in the bar
    await expect(bar.getByRole('button', { name: /^(Delete|Archive|Restore)\b/ }), `${s} destructive in More`).toHaveCount(0);
    for (const a of RECORD_SURFACES[s].actions.filter((x) => !MENU_ACTIONS.includes(x))) await expect(bar.locator(`[data-action="${a}"]`).first(), `${s} ${a}`).toBeVisible();
    const inMenu = RECORD_SURFACES[s].actions.filter((x) => MENU_ACTIONS.includes(x));
    if (inMenu.length) {
      await bar.getByTestId('more-actions').click();
      for (const a of inMenu) await expect(page.getByRole('menu').locator(`[data-action="${a}"]`), `${s} ${a} in More`).toBeVisible();
      await shot(`${s}-more`);
      await page.keyboard.press('Escape');
      await expect(page.getByRole('menu')).toHaveCount(0);
    }
    await shot(s);
  }

  // Delete names the item
  await page.goto(`/sequfloh/parts/${part}`);
  await view(page).locator('[data-record-actions="part"]').getByTestId('more-actions').click();
  await page.getByRole('menu').locator('[data-action="delete"]').click();
  await expect(page.getByRole('dialog')).toContainText(`RA-${stamp}`);
  await shot('delete-names-item');
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  // Save and Cancel follow unsaved changes; Cancel asks first
  await page.goto(`/sequfloh/chargebacks/${cb}`);
  const bar = view(page).locator('[data-record-actions="chargeback-report-edit"]');
  await expect(bar.locator('[data-action="save"]')).toBeDisabled();
  await view(page).getByLabel('Supplier', { exact: true }).fill('Changed Supplier Co');
  await expect(bar).toHaveAttribute('data-unsaved', 'true');
  await expect(bar.locator('[data-action="save"]')).toBeEnabled();

  // leaving through a link asks; dismissing keeps the page
  let asked = '';
  page.once('dialog', async (d) => { asked = d.message(); await d.dismiss(); });
  await page.locator('a[href="/sequfloh/chargebacks"]').first().click();
  await expect.poll(() => asked).toContain('unsaved changes');
  await expect(page).toHaveURL(new RegExp(`/sequfloh/chargebacks/${cb}$`));

  await bar.locator('[data-action="cancel"]').click();
  await expect(page.getByRole('dialog')).toContainText('Discard unsaved changes?');
  await shot('cancel-asks');
  await page.getByRole('dialog').getByRole('button', { name: 'Discard changes' }).click();
});

/** Every form dialog uses FormDialogTitle (title + Full screen); only the confirm dialog in common.tsx keeps a plain title. */
test('no form dialog without Full screen', () => {
  const root = path.resolve('src');
  const walk = (d: string): string[] => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith('.tsx') ? [path.join(d, e.name)] : []);
  const bad = walk(root).filter((f) => !f.endsWith(path.join('components', 'FullScreen.tsx')) && !f.endsWith(path.join('sequfloh', 'common.tsx')) && /<DialogTitle[\s>]/.test(fs.readFileSync(f, 'utf8')));
  expect(bad.map((f) => path.relative(root, f))).toEqual([]);
  expect(FULLSCREEN_VIEWERS).toContain('drawing-viewer');
});

/** Full screen keeps the toolbar, unsaved edits and scroll position; Exit full screen and Esc both put the page back. Made-up records only. */
test('full screen on a record page, a form dialog and the drawing viewer', async ({ page }) => {
  test.setTimeout(120_000);
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `fs-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `FS instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-FS${stamp}` } }))).id as string;
  const cb = (await ok(rq.post('/api/sequfloh/chargebacks', { data: { workOrderNumber: `WO-FS${stamp}`, trackingRecordId: wo, supplier: 'Example Supplier Co' } }))).id as string;
  const folder = (await ok(rq.post('/api/drawings/folders', { data: { name: `FS ${stamp}`, siteId: site } }))).id as string;
  const drawing = (await ok(rq.post(`/api/drawings/folders/${folder}/drawings`, { multipart: {
    file: { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n') }, title: 'Test bracket', drawingNumber: `DWG-FS${stamp}`, revision: 'A' } }))).id as string;

  // record page with unsaved edits
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto(`/sequfloh/chargebacks/${cb}`);
  const v = view(page);
  const bar = v.locator('[data-record-actions="chargeback-report-edit"]');
  const area = v.locator('main[data-fullscreen-root]');
  await v.getByLabel('Supplier', { exact: true }).fill('Typed but not saved');
  await page.evaluate(() => window.scrollTo(0, 40)); // a little down, toolbar still on screen
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(0);
  const before = await page.evaluate(() => ({ y: window.scrollY, box: (document.querySelector('[data-testid^="tab-view-"][data-active="true"]') as HTMLElement).getBoundingClientRect().toJSON() }));
  await bar.getByTestId('fullscreen-toggle').click();
  await expect(area).toHaveAttribute('data-fullscreen', /native|css/);
  await expect(bar.locator('[data-action="save"]')).toBeVisible();
  await expect(bar.getByTestId('fullscreen-toggle')).toHaveText('Exit full screen');
  await expect(v.getByLabel('Supplier', { exact: true })).toHaveValue('Typed but not saved');
  const box = await area.boundingBox(); const vp = page.viewportSize()!;
  expect([box!.x, box!.y, Math.round(box!.width), Math.round(box!.height)]).toEqual([0, 0, vp.width, vp.height]);
  await bar.getByTestId('fullscreen-toggle').click();
  await expect(area).not.toHaveAttribute('data-fullscreen', /.*/);
  await expect(v.getByLabel('Supplier', { exact: true })).toHaveValue('Typed but not saved');
  await expect(bar).toHaveAttribute('data-unsaved', 'true');
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(before.y);
  const after = await page.evaluate(() => (document.querySelector('[data-testid^="tab-view-"][data-active="true"]') as HTMLElement).getBoundingClientRect().toJSON());
  expect(after).toEqual(before.box);

  // Esc also restores
  await bar.getByTestId('fullscreen-toggle').click();
  await expect(area).toHaveAttribute('data-fullscreen', /native|css/);
  await page.keyboard.press('Escape');
  if (await area.getAttribute('data-fullscreen') === 'native') await page.evaluate(() => document.exitFullscreen());
  await expect(area).not.toHaveAttribute('data-fullscreen', /.*/);
  await expect(v.getByLabel('Supplier', { exact: true })).toHaveValue('Typed but not saved');
  page.once('dialog', (d) => d.accept());
  await bar.locator('[data-action="cancel"]').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Discard changes' }).click();
  await expect(page).not.toHaveURL(new RegExp(`/chargebacks/${cb}$`));

  // a form dialog
  await page.goto(`/sequfloh/drawings/d/${drawing}`);
  await view(page).locator('[data-record-actions="drawing"] [data-action="edit"]').click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('fullscreen-toggle').click();
  await expect(page.locator('.MuiDialog-paper[data-fullscreen]')).toHaveCount(1);
  await shot('03-dialog-full-screen');
  await dlg.getByTestId('fullscreen-toggle').click();
  await expect(page.locator('.MuiDialog-paper[data-fullscreen]')).toHaveCount(0);
  await expect(dlg).toBeVisible();
  await page.keyboard.press('Escape');

  // the drawing viewer
  await page.goto(`/drawings/view/${drawing}`);
  const vb = page.locator('[data-fullscreen-viewer="drawing-viewer"]').getByTestId('fullscreen-toggle');
  await expect(vb).toBeVisible();
  await vb.click();
  await expect(page.locator('[data-fullscreen]')).toHaveCount(1);
  await shot('04-drawing-viewer-full-screen');
  await vb.click();
  await expect(page.locator('[data-fullscreen]')).toHaveCount(0);
});

/** Screenshots, normal and full screen, with the window-filling layout used when the browser does not allow real full screen. */
test('full screen screenshots (window-filling fallback)', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(Document.prototype, 'fullscreenEnabled', { get: () => false }));
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `fs-${name}.png`)); };
  await signIn(page);
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto('/sequfloh/chargebacks/new');
  const v = view(page);
  const area = v.locator('main[data-fullscreen-root]');
  await v.getByLabel('Supplier', { exact: true }).fill('Example Supplier Co');
  await shot('01-normal');
  await v.getByTestId('fullscreen-toggle').click();
  await expect(area).toHaveAttribute('data-fullscreen', 'css');
  const box = await area.boundingBox();
  expect([box!.x, box!.y, Math.round(box!.width), Math.round(box!.height)]).toEqual([0, 0, 1280, 720]);
  await shot('02-full-screen');
  await page.keyboard.press('Escape');
  await expect(area).not.toHaveAttribute('data-fullscreen', /.*/);
  await expect(v.getByLabel('Supplier', { exact: true })).toHaveValue('Example Supplier Co');
  await shot('05-back-to-normal');
  page.on('dialog', (d) => d.accept());
});

/**
 * Work orders without a work instruction, one-click drawings, not-applicable part fields, Completed Validation / FAI and named day workers. Made-up records only.
 */
test('fix list: optional instruction, drawings, hidden part fields, completed forms and day workers', async ({ page }) => {
  test.setTimeout(180_000);
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;

  // a work order made with no instruction; one is added and removed later from the record page
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Optional instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', siteId: site, workOrder: `WO-FX${stamp}` } }))).id as string;
  await page.goto(`/sequfloh/record/${wo}`);
  const ref = view(page).getByTestId('wo-instruction').first();
  await expect(ref).toContainText('—');
  await ref.getByTestId('wo-instruction-change').click();
  const dlg = page.getByRole('dialog');
  await dlg.getByLabel('Released work instruction').fill(`Optional instruction ${stamp}`);
  await page.getByRole('option', { name: new RegExp(`Optional instruction ${stamp}`) }).click();
  await dlg.getByRole('button', { name: 'Save' }).click();
  await expect(ref).toContainText(`Optional instruction ${stamp}`);
  await ref.getByTestId('wo-instruction-remove').click();
  await expect(ref).not.toContainText(`Optional instruction ${stamp}`);

  // day workers: typed names, each with their own clock, plus entered time
  await view(page).getByRole('tab', { name: 'Time' }).click().catch(() => {});
  const dw = view(page).locator('[data-testid="day-workers"]:visible').first();
  await expect(async () => { await dw.getByTestId('day-worker-name').fill(`Made-up Pat ${stamp}`); await dw.getByTestId('day-worker-add').click({ timeout: 2000 }); }).toPass({ timeout: 30_000 });
  await expect(dw.locator(`[data-testid="day-worker-row"][data-name="Made-up Pat ${stamp}"]`)).toBeVisible();
  await dw.getByTestId('day-worker-name').fill(`Made-up Lee ${stamp}`);
  await dw.getByTestId('day-worker-name').press('Enter');
  const pat = dw.locator(`[data-testid="day-worker-row"][data-name="Made-up Pat ${stamp}"]`);
  const lee = dw.locator(`[data-testid="day-worker-row"][data-name="Made-up Lee ${stamp}"]`);
  await expect(pat).toBeVisible(); await expect(lee).toBeVisible();
  await pat.getByTestId('day-worker-start').click();
  await expect(pat.getByTestId('day-worker-pause')).toBeVisible();
  await expect(lee.getByTestId('day-worker-start')).toBeVisible(); // Lee's clock is separate
  await pat.getByTestId('day-worker-pause').click();
  await pat.getByTestId('day-worker-continue').click();
  await pat.getByTestId('day-worker-stop').click();
  await expect(pat.getByTestId('day-worker-start')).toBeVisible();
  await lee.getByTestId('day-worker-add-time').click();
  const d = new Date(Date.now() - 2 * 86400_000); const p2 = (n: number) => String(n).padStart(2, '0');
  const at = (h: number) => `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}T${p2(h)}:00`;
  await page.getByTestId('session-start').fill(at(8)); await page.getByTestId('session-stop').fill(at(10));
  await page.getByTestId('session-save').click();
  await expect(lee.getByTestId('day-worker-hours')).toHaveText('2.00');
  await expect(view(page).getByTestId('crew-day-hours').first()).not.toHaveText('0.00');
  await expect(page.locator('body')).not.toContainText(/\bpay\b/i);
  // recorded time is never deleted: workers with time have no Remove, and no time row has a delete
  await expect(lee.getByTestId('day-worker-remove')).toHaveCount(0);
  await expect(pat.getByTestId('day-worker-remove')).toHaveCount(0);
  await expect(dw.getByRole('button', { name: /^Delete time/ })).toHaveCount(0);

  // drawings: one click opens the viewer; the detail page is behind Details; a single-drawing folder shows it inline
  const folder = (await ok(rq.post('/api/drawings/folders', { data: { name: `Fix ${stamp}`, siteId: site } }))).id as string;
  const drawing = (await ok(rq.post(`/api/drawings/folders/${folder}/drawings`, { multipart: {
    file: { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n') }, title: 'Test bracket', drawingNumber: `DWG-FX${stamp}`, revision: 'A' } }))).id as string;
  await page.goto(`/sequfloh/drawings?folder=${folder}`);
  await expect(view(page).getByTestId('folder-inline-drawing')).toBeVisible();
  const open = view(page).getByTestId('drawing-open').first();
  await expect(open).toHaveAttribute('target', '_blank');
  await view(page).getByTestId('drawing-details').first().click();
  await expect(page).toHaveURL(new RegExp(`/sequfloh/drawings/d/${drawing}`));

  // a part with Material marked not applicable hides it until Show hidden fields
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `FX-${stamp}`, name: 'Test spacer', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false, material: 'Made-up steel' },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false }, hiddenFields: ['material'] } }))).id as string;
  await ok(rq.put(`/api/drawings/for-part/${part}`, { data: { ids: [drawing] } }));
  await page.goto(`/sequfloh/parts/${part}`);
  await expect(view(page).getByTestId('view-drawing').first()).toBeVisible();
  await expect(view(page).getByText('Made-up steel')).toHaveCount(0);
  await view(page).getByTestId('show-hidden-fields').click();
  await expect(view(page).getByText('Made-up steel').first()).toBeVisible();

  // Completed Validation / FAI: a filled copy is saved under a family, listed on that tab, and can be moved to Unassigned from Edit
  const blank = (await ok(rq.post('/api/imported-files', { multipart: {
    file: { name: `fai-${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('Check,Result\nTorque,\n') }, siteId: site, blankForm: 'true' } }))).id as string;
  const fam = ((await ok(rq.get('/api/sequfloh/reference/product-families'))) as { id: string; label: string; isActive: boolean }[]).find((f) => f.isActive)!;
  await page.goto(`/sequfloh/imported-files/${blank}`);
  await view(page).getByTestId('fill-out').click();
  const cd = page.getByRole('dialog');
  await cd.getByTestId('copy-number').fill(`FAI-COPY-${stamp}`);
  await cd.getByTestId('copy-title').fill(`FAI copy ${stamp}`);
  await cd.getByLabel('Product family').click();
  await page.getByRole('option', { name: fam.label, exact: true }).click();
  await cd.getByTestId('copy-confirm').click();
  await page.waitForURL((u) => u.pathname.startsWith('/sequfloh/imported-files/') && !u.pathname.endsWith(blank));
  await page.goto('/sequfloh/completed-forms');
  await page.getByRole('tab', { name: fam.label, exact: true }).click();
  const row = view(page).getByRole('row', { name: new RegExp(`FAI copy ${stamp}`) });
  await expect(row).toBeVisible();
  await expect(row.getByTestId('completed-print')).toBeVisible();
  await page.goto(`/sequfloh/imported-files/${await row.getByTestId('completed-open').getAttribute('data-id')}`);
  await (await more(page)).getByTestId('change-family').click();
  await page.getByRole('dialog').getByLabel('Product family').click();
  await page.getByRole('option', { name: 'Unassigned' }).click();
  await page.getByTestId('family-save').click();
  await page.goto('/sequfloh/completed-forms?family=none');
  await expect(view(page).getByRole('row', { name: new RegExp(`FAI copy ${stamp}`) })).toBeVisible();
});
