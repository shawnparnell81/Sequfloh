import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Inspection steps come only from a linked instruction; uploaded instructions are their file and open in a new tab; Remove WI from order keeps recorded steps. */
test('work instructions on work orders: no placeholder step, uploaded file opens inline, remove and relink', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const today = new Date().toISOString().slice(0, 10);
  const release = async (id: string) => ok(rq.post(`/api/sequfloh/instructions/${id}/actions/release`, { data: { comment: null, effectiveDate: today } }));
  const order = async (number: string, wi: string | null) => (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Inspection', number, workInstructionId: wi, siteId: site, inspectionDescription: 'Example housings from bin 4' } }))).id as string;

  // 1. an inspection order with no instruction has no placeholder step
  const bare = await order(`E2E-INS-${k}-1`, null);
  await page.goto(`/sequfloh/record/${bare}`);
  await view(page).getByRole('tab', { name: 'Steps' }).click();
  await expect(view(page).getByTestId('steps-none')).toHaveText('No steps - link a WI / Procedure to load steps.');
  await shot('01-inspection-order-no-wi-no-steps');

  // 2. an uploaded instruction is only its file
  const fs = await import('node:fs');
  const up = await ok(rq.post('/api/sequfloh/instructions/import', { multipart: { file: { name: `Example housing inspection ${k}.pdf`, mimeType: 'application/pdf', buffer: fs.readFileSync(new URL('./example-instruction.pdf', import.meta.url)) },
    type: 'Inspection', siteId: site } }));
  await release(up.id);
  await page.goto(`/sequfloh/instruction/${up.id}`);
  await expect(view(page).getByTestId('wi-file-open')).toBeVisible();
  await expect(view(page).getByRole('tab', { name: 'Layout' })).toHaveCount(0);
  await expect(view(page).getByRole('tab', { name: 'Content' })).toHaveCount(0);
  await shot('02-uploaded-wi-file-only');

  // 3. the linked uploaded instruction opens its file inline in a new tab
  const withFile = await order(`E2E-INS-${k}-2`, up.id);
  await page.goto(`/sequfloh/record/${withFile}`);
  const link = view(page).getByTestId('wo-instruction-open');
  await expect(link).toHaveAttribute('target', '_blank');
  const href = (await link.getAttribute('href'))!;
  expect(href).toMatch(/^\/api\/files\/[0-9a-f-]{36}$/);
  const file = await rq.get(href);
  expect(file.headers()['content-type']).toBe('application/pdf');
  expect(file.headers()['content-disposition']).toMatch(/^inline/);
  await shot('03-order-linked-uploaded-wi');
  await view(page).getByRole('tab', { name: 'Steps' }).click();
  await expect(view(page).getByTestId('steps-none')).toBeVisible();

  // 4. an app-built instruction opens read only; Remove WI from order keeps the recorded step
  const built = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Inspection', title: `Example built inspection ${k}`, number: `E2E-WI-${k}`, siteId: site, departmentId: null, partId: null } }))).id as string;
  await ok(rq.put(`/api/sequfloh/instructions/${built}`, { data: { title: `Example built inspection ${k}`, siteId: site, steps: [{ sequence: 10, title: 'Check the housing for cracks' }, { sequence: 20, title: 'Count the good parts' }] } }));
  await release(built);
  const run = await order(`E2E-INS-${k}-3`, built);
  const rec = async () => (await ok(rq.get(`/api/sequfloh/tracking/${run}`))).record;
  await ok(rq.post(`/api/sequfloh/tracking/${run}/actions/start`, { data: { operationId: crypto.randomUUID(), stateVersion: (await rec()).stateVersion } }));
  const r = await rec();
  await ok(rq.put(`/api/sequfloh/tracking/${run}`, { data: { operationId: crypto.randomUUID(), stateVersion: r.stateVersion, steps: [{ id: r.steps[0].id, result: 'Pass', comment: 'No cracks' }] } }));
  await page.goto(`/sequfloh/record/${run}`);
  await expect(view(page).getByTestId('wo-instruction-open')).toHaveAttribute('href', `/sequfloh/instruction/${built}`);
  await view(page).getByTestId('wo-instruction-remove').click();
  await page.getByTestId('wo-instruction-remove-reason').fill('Crack limit is changing in the next revision');
  await shot('04-remove-wi-from-order');
  await page.getByTestId('wo-instruction-remove-confirm').click();
  await expect(view(page).getByTestId('wo-instruction-change')).toHaveText('Add work instruction');
  await view(page).getByRole('tab', { name: 'Steps' }).click();
  await expect(view(page).getByText('Check the housing for cracks')).toBeVisible();
  await expect(view(page).getByText('Count the good parts')).toHaveCount(0);
  await shot('05-after-remove-recorded-step-kept');
  const after = await rec();
  expect(after.workInstructionId).toBeNull();
  expect(after.steps[0].keptFrom).toContain('rev A');
});
