import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Pictures and files on create, job notes on the main page, findings Yes/No and the close reminder. */
test('job notes, pictures and files, findings question and close reminder', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const png = path.join(test.info().outputDir, 'made-up-photo.png');
  fs.mkdirSync(path.dirname(png), { recursive: true });
  fs.writeFileSync(png, Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAADVq7jyAAAAQklEQVR42u3PMQ0AAAgDoGlx/S0twEEPCDRSqXojICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIPBdA2rhRJjl7JhBAAAAAElFTkSuQmCC', 'base64'));
  const pdf = path.join(test.info().outputDir, 'made-up-spec.pdf');
  fs.writeFileSync(pdf, '%PDF-1.4\n% made up\n');

  await page.goto('/sequfloh/tracking/new?type=Inspection');
  await page.getByTestId('create-clock-no').click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByLabel('Number *').fill(`E2E-${Math.random().toString(36).slice(2, 10)}`);
  await page.getByLabel('Inspection sub-type').click();
  await page.getByRole('option').first().click();
  await page.getByTestId('new-wo-files-input').setInputFiles([png, pdf]);
  await expect(page.getByTestId('new-wo-files')).toContainText('made-up-spec.pdf');
  await page.getByTestId('new-wo-files').scrollIntoViewIfNeeded();
  await shot('01-create-pictures-and-files');
  await page.getByTestId('new-wo-create').click();
  await page.waitForURL(/\/sequfloh\/record\/[0-9a-f-]{36}/);
  const id = page.url().split('/').pop()!.split('?')[0];

  const files = view(page).getByTestId('record-files');
  await expect(files.getByTestId('record-picture')).toHaveCount(1);
  await expect(files.getByTestId('record-file')).toContainText('made-up-spec.pdf');
  const notes = view(page).getByTestId('job-notes');
  await notes.getByTestId('job-note-text').fill('Made-up: first half of the lot inspected, 3 rejects bagged on rack B.');
  await notes.getByTestId('job-note-add').click();
  await expect(notes.getByTestId('job-note')).toHaveCount(1);
  await notes.getByTestId('job-note-text').fill('Made-up: second shift, finish the remaining 40 pieces.');
  await notes.getByTestId('job-note-add').click();
  await expect(notes.getByTestId('job-note').first()).toContainText('second shift');
  await shot('02-main-page-job-notes-and-files');

  await view(page).getByRole('tab', { name: 'Findings / Nonconformance' }).click();
  await expect(view(page).getByTestId('findings-section')).toHaveCount(0);
  await shot('03-findings-question-no');
  await view(page).getByTestId('findings-yes').click();
  await expect(view(page).getByTestId('findings-section')).toBeVisible();
  await shot('04-findings-question-yes');

  await ok(rq.post(`/api/sequfloh/tracking/${id}/actions/start`, { data: { operationId: crypto.randomUUID(), stateVersion: (await ok(rq.get(`/api/sequfloh/tracking/${id}`))).record.stateVersion } }));
  await page.reload();
  await view(page).getByRole('button', { name: 'Complete', exact: true }).first().click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByTestId('close-note-reminder')).toContainText('Add notes or comments before closing?');
  const variance = dlg.getByLabel(/Variance reason/); if (await variance.count()) await variance.fill('Made-up: short test job');
  await dlg.getByTestId('close-note-text').fill('Made-up: all done, rejects sent to MRB.');
  await shot('05-close-reminder');
  await dlg.getByTestId('close-with-note').click();
  await expect(dlg).toHaveCount(0);
  const list = await ok(rq.get(`/api/sequfloh/tracking/${id}/job/notes`));
  expect(list[0].text).toBe('Made-up: all done, rejects sent to MRB.');
  expect((await ok(rq.get(`/api/sequfloh/tracking/${id}`))).record.status).toBe('Completed');
});
