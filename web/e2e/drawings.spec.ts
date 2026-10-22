import path from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** Made-up drawings, drawn on the fly: nothing here is a real part or a real drawing. */
const svg = (title: string, rev: string) => `<html><body style="margin:0;background:#fff">
  <svg xmlns="http://www.w3.org/2000/svg" width="1100" height="700" viewBox="0 0 1100 700" font-family="Arial">
    <rect x="10" y="10" width="1080" height="680" fill="none" stroke="#000" stroke-width="3"/>
    <rect x="250" y="160" width="500" height="260" fill="none" stroke="#000" stroke-width="2"/>
    <circle cx="380" cy="290" r="45" fill="none" stroke="#000" stroke-width="2"/><circle cx="620" cy="290" r="45" fill="none" stroke="#000" stroke-width="2"/>
    <line x1="250" y1="460" x2="750" y2="460" stroke="#000"/><text x="470" y="485" font-size="18">100.0 ±0.1</text>
    <rect x="700" y="560" width="380" height="120" fill="none" stroke="#000" stroke-width="2"/>
    <text x="715" y="595" font-size="20">${title}</text><text x="715" y="630" font-size="16">MADE-UP TEST DRAWING · NOT FOR USE</text>
    <text x="715" y="665" font-size="22">REV ${rev}</text></svg></body></html>`;
async function png(browser: Browser, title: string, rev: string) {
  const p = await browser.newPage({ viewport: { width: 1100, height: 700 } }); await p.setContent(svg(title, rev)); const b = await p.screenshot(); await p.close(); return b;
}
async function pdf(browser: Browser, title: string, rev: string) {
  const p = await browser.newPage(); await p.setContent(svg(title, rev)); const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

test('drawings: folders, revisions, two-way part links, Open drawing from a filled form and from a validation work order', async ({ page, browser, context }) => {
  test.skip(!process.env.E2E_CF_XLSX, 'set E2E_CF_XLSX');
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string, p: Page = page) => { n++; if (shots) await settledShot(p, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const stamp = Date.now().toString().slice(-6);
  const part = `TP-DRW-${stamp}`;
  const dwg = `DWG-${stamp}`;
  await signIn(page);

  // Engineering > Drawings
  await section(page, 'Engineering');
  await page.getByRole('navigation', { name: 'Engineering pages' }).getByRole('link', { name: 'Drawings' }).click();
  await expect(view(page).getByRole('heading', { name: 'Drawings' }).first()).toBeVisible();
  await shot('engineering-drawings-page');

  await view(page).getByRole('button', { name: 'New folder' }).click();
  await page.getByLabel('Folder name').fill(`Brackets ${stamp}`);
  await shot('new-folder');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(view(page).getByText('This folder is empty.')).toBeVisible();

  // upload revision A (PDF) and revision B (PNG)
  await view(page).getByRole('button', { name: 'Upload drawing' }).click();
  await page.getByTestId('drawing-file').setInputFiles({ name: 'test-bracket.pdf', mimeType: 'application/pdf', buffer: await pdf(browser, 'TEST BRACKET', 'A') });
  await page.getByLabel('Title').fill('Test bracket');
  await page.getByLabel('Drawing number').fill(dwg);
  await shot('upload-drawing');
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  await expect(view(page).getByTestId('current-rev')).toHaveText('A');
  await view(page).getByRole('button', { name: 'New revision' }).click();
  await page.getByTestId('revision-file').setInputFiles({ name: 'test-bracket-b.png', mimeType: 'image/png', buffer: await png(browser, 'TEST BRACKET', 'B') });
  await page.getByLabel('What changed').fill('Hole spacing changed (made-up change)');
  await shot('new-revision');
  await page.getByRole('button', { name: 'Upload revision' }).click();
  await expect(view(page).getByTestId('current-rev')).toHaveText('B');
  await expect(view(page).getByText('Current', { exact: true })).toBeVisible();
  await shot('drawing-revision-history');
  const drawingUrl = page.url();

  // new part: link the drawing while creating it
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'New part' }).click();
  await page.getByLabel('Part number').fill(part);
  await page.getByLabel('Name').fill('Test bracket (made up)');
  await page.getByLabel('Link drawing(s)').fill(dwg);
  await page.getByRole('option', { name: new RegExp(dwg) }).click();
  await shot('new-part-link-drawing');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(view(page).getByTestId('part-drawings')).toContainText(`(rev B)`);
  await shot('part-page-linked-drawing');

  // the drawing shows the link too; a second drawing is uploaded with 'Link to part number(s)'
  await page.goto(drawingUrl);
  await expect(view(page).getByTestId('linked-parts')).toContainText(part);
  await shot('drawing-shows-linked-part');
  await view(page).getByRole('link', { name: `Brackets ${stamp}` }).click();
  await view(page).getByRole('button', { name: 'Upload drawing' }).click();
  await page.getByTestId('drawing-file').setInputFiles({ name: 'test-bracket-section.png', mimeType: 'image/png', buffer: await png(browser, 'TEST BRACKET SECTION', 'A') });
  await page.getByLabel('Title').fill('Test bracket section');
  await page.getByLabel('Drawing number').fill(`${dwg}-S`);
  await page.getByLabel('Link to part number(s)').fill(part);
  await page.getByRole('option', { name: new RegExp(part) }).click();
  await shot('upload-with-link-to-part');
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  await expect(view(page).getByTestId('linked-parts')).toContainText(part);

  // search by part number
  await page.goto('/sequfloh/engineering/drawings');
  await view(page).getByLabel('Search drawings').fill(part);
  await view(page).getByRole('button', { name: 'Search' }).click();
  await expect(view(page).getByText('Test bracket section')).toBeVisible();
  await shot('search-by-part-number');

  // no work order: fill out a blank form for the part and tick Open drawing
  await section(page, 'Imported Files');
  await view(page).getByTestId('import-file').click();
  await page.getByTestId('import-input').setInputFiles(path.resolve(process.env.E2E_CF_XLSX!));
  await page.getByLabel('Title').fill(`Drawing check blank ${stamp}`);
  await page.getByTestId('import-upload').click();
  await view(page).getByTestId('fill-out').click();
  await page.getByTestId('copy-part-number').fill('NOPE-0000');
  await expect(page.getByTestId('no-drawing')).toBeVisible();
  await page.getByTestId('copy-part-number').fill(part);
  const box = page.getByRole('checkbox', { name: `Open drawing ${dwg}`, exact: true });
  await expect(box).toBeVisible();
  await shot('fill-out-part-number-drawing-checkboxes');
  const [viewer] = await Promise.all([context.waitForEvent('page'), box.check()]);
  await expect(viewer.getByTestId('viewer-title')).toContainText(`${dwg} · Test bracket · rev B`);
  await expect(viewer.getByTestId('drawing-image')).toBeVisible();
  await shot('drawing-opened-in-new-window', viewer);
  await viewer.close();
  await page.getByTestId('copy-confirm').click();
  await expect(view(page).getByTestId('copy-part')).toContainText(part);
  await shot('filled-copy-header-open-drawing');
  const [again] = await Promise.all([context.waitForEvent('page'), view(page).getByTestId('copy-part').getByTestId('open-drawing').first().click()]);
  await expect(again.getByTestId('viewer-title')).toContainText('rev');
  await again.close();

  // validation work order: the part's drawings are attached when it is created
  await page.goto('/sequfloh/tracking/new?type=Validation');
  await view(page).getByLabel('Released work instruction').click();
  await page.getByRole('option').first().click();
  await view(page).getByLabel('Part', { exact: true }).fill(part);
  await page.getByRole('option', { name: new RegExp(part) }).click();
  await expect(view(page).getByTestId('new-record-drawings')).toContainText('attached to the record');
  await shot('new-validation-record-part-drawings');
  await view(page).getByRole('button', { name: 'Create record' }).click();
  await expect(view(page).getByTestId('record-drawings')).toContainText('Rev B (attached)');
  await shot('work-order-drawings-attached');

  // a new revision later: the order notes the attached revision and opens the current one
  const id = drawingUrl.split('/').pop()!;
  const r = await page.request.post(`/api/drawings/${id}/revisions`, { multipart: { revision: 'C', notes: 'Made-up revision C',
    file: { name: 'test-bracket-c.png', mimeType: 'image/png', buffer: await png(browser, 'TEST BRACKET', 'C') } } });
  expect(r.ok()).toBeTruthy();
  await page.reload();
  await expect(view(page).getByTestId('record-drawings')).toContainText('Attached rev B · now rev C');
  await shot('work-order-attached-rev-and-current-rev');
  const [cur] = await Promise.all([context.waitForEvent('page'), view(page).getByTestId('record-drawings').getByRole('link', { name: new RegExp(`Open drawing ${dwg} \\(rev C\\)`) }).click()]);
  await expect(cur.getByTestId('viewer-title')).toContainText('rev C');
  await shot('work-order-open-current-drawing', cur);
  await cur.close();
});
