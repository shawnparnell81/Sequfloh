import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** A made-up one-page drawing as a PDF; nothing here is a real part or drawing. */
async function pdf(browser: Browser, title: string) {
  const p = await browser.newPage();
  await p.setContent(`<html><body style="font-family:Arial;border:3px solid #000;height:640px;padding:20px"><h1>${title}</h1><p>MADE-UP TEST DRAWING · NOT FOR USE</p></body></html>`);
  const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

async function importParts(page: Page, csv: string, count: number) {
  const v = view(page);
  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-fuel-cross.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await imp.getByRole('combobox', { name: /^Name/ }).click(); await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await imp.getByRole('button', { name: 'Preview' }).click();
  await imp.getByRole('button', { name: `Import ${count} parts` }).click();
  await expect(imp.getByTestId('import-done')).toContainText(`${count} added`, { timeout: 60_000 });
  await imp.getByRole('button', { name: 'Close' }).click();
}

test('drawings: a JH PN drawing links to every fuel pump that shares it, on import, on parts import and with Relink; search by DMA or Cross reaches it', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const shots = process.env.E2E_JH_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  const or = (n: number) => `JL${k}-${n}`;
  await signIn(page);
  const v = view(page);

  // 7 made-up fuel pumps share JH PN JH-A<k>; 2 more share JH-B<k>, and their drawing comes in before them
  await importParts(page, `OR PN,NAPA PN,DMA PN,JH PN\n${Array.from({ length: 7 }, (_, n) => `${or(n)},NP${k}${n},DM${k}${n},JH-A${k}`).join('\n')}\n`, 7);

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-jh-'));
  const top = path.join(root, `Fuel pump drawings ${k}`);
  fs.mkdirSync(top, { recursive: true });
  fs.writeFileSync(path.join(top, `JH A${k}.pdf`), await pdf(browser, `JH-A${k}`));
  fs.writeFileSync(path.join(top, `JH-B${k}.pdf`), await pdf(browser, `JH-B${k}`));

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  const row = dlg.getByRole('row').filter({ hasText: `JH A${k}.pdf` });
  await expect(row.getByTestId('match-count')).toHaveText('7 parts');
  await expect(row).toContainText('and 1 more');
  await shot('01-import-preview-jh-7-parts');
  await dlg.getByRole('button', { name: 'Import 2 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('7 part links', { timeout: 60_000 });
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the parts for JH-B arrive afterwards: the parts import links them to the drawing already here
  await importParts(page, `OR PN,NAPA PN,DMA PN,Cross,JH PN\n${or(50)},/,DM${k}50,CR${k}50,JH-B${k}\n${or(51)},/,DM${k}51,CR${k}51,JH-B${k}\n`, 2);

  await section(page, 'Parts Drawings');
  // searching by a DMA PN or by a Cross number reaches the drawing; its page lists every number of each linked part
  const search = v.getByRole('textbox', { name: 'Search drawings' });
  await search.fill(`DM${k}51`); await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.getByRole('cell', { name: `JH-B${k}`, exact: true })).toBeVisible();
  await search.fill(`CR${k}50`); await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.getByRole('cell', { name: `JH-B${k}`, exact: true })).toBeVisible();
  await v.getByRole('row').filter({ hasText: `JH-B${k}` }).getByRole('button').first().click();
  const linked = v.getByTestId('linked-parts');
  await expect(linked).toContainText(`${or(50)} · DMA PN DM${k}50 · Cross CR${k}50 · JH PN JH-B${k}`);
  await expect(linked).toContainText(`${or(51)} · DMA PN DM${k}51`);
  await expect(linked).not.toContainText('NAPA PN /');
  await shot('02-drawing-linked-after-parts-import');

  // the part page shows its drawing at the top
  await linked.getByText(`${or(51)} ·`, { exact: false }).click();
  await expect(v.getByTestId('part-drawings-box')).toContainText(`JH-B${k}`);
  await shot('03-part-page-drawing');

  // Relink checks every drawing; nothing is missing now, so a run adds no links and removes none
  await page.goto('/sequfloh/drawings');
  await v.getByRole('button', { name: 'Relink drawings' }).click();
  const rl = page.getByRole('dialog');
  await rl.getByRole('button', { name: 'Relink' }).click();
  await expect(rl.getByTestId('relink-result')).toContainText(/Checked [\d,]+ drawings\. Added 0 part links\./);
  await shot('04-relink-result');
  await rl.getByRole('button', { name: 'Close' }).click();
});
