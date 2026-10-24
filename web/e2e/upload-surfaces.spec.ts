import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { signIn, view } from './support';
import { SINGLE_FILE_INPUTS, UPLOAD_SURFACES } from '../src/components/uploadSurfaces';

/**
 * Regression guard: every page that takes uploads offers both Import file and Import folder through the shared ImportButtons component.
 * A new upload page must be added to UPLOAD_SURFACES (and so to this check); a new bare file input fails the static scan below.
 */
const src = new URL('../src', import.meta.url).pathname;
const walk = (d: string): string[] => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : /\.tsx?$/.test(e.name) ? [path.join(d, e.name)] : []);

test('no file input outside the shared import buttons or the listed single-file replacements', () => {
  const bad: string[] = [];
  for (const f of walk(src)) {
    if (f.endsWith(path.join('components', 'ImportButtons.tsx'))) continue;
    const text = fs.readFileSync(f, 'utf8');
    for (const m of (text.match(/<input\b[\s\S]*?\/>/g) ?? []).filter((x) => x.includes('type="file"'))) {
      const id = /data-testid\W+([\w-]+)/.exec(m)?.[1];
      if (!id || !(id in SINGLE_FILE_INPUTS)) bad.push(`${path.relative(src, f)}: ${m.slice(0, 120)}`);
    }
  }
  expect(bad, 'Use ImportButtons, or list a single-file replacement in SINGLE_FILE_INPUTS with its reason').toEqual([]);
});

test('every upload page shows Import file and Import folder', async ({ page }) => {
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Guard instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  const draft = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Guard draft ${stamp}`, siteId: site } }))).id as string;
  const ir = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { siteId: site, partNumber: `GUARD-${stamp}`, priority: 'Normal', requestedDate: '2026-11-20' } }))).id as string;
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-G${stamp}` } }))).id as string;

  const visits: Record<string, () => Promise<void>> = {
    'imported-files': async () => { await page.goto('/sequfloh/imported-files'); },
    'parts-drawings': async () => { await page.goto('/sequfloh/drawings'); },
    'work-instructions': async () => { await page.goto('/sequfloh/instructions/validation'); },
    'work-instruction-files': async () => { await page.goto(`/sequfloh/instruction/${draft}`); await view(page).getByRole('tab', { name: 'Layout' }).click(); },
    'inspection-request-files': async () => { await page.goto(`/sequfloh/inspection-request/${ir}`); },
    'work-order-tester-files': async () => { await page.goto(`/sequfloh/record/${wo}`); await view(page).getByRole('tab', { name: 'Job' }).click(); },
    'work-order-evidence': async () => { await page.goto(`/sequfloh/record/${wo}`); await view(page).getByRole('tab', { name: 'Findings' }).click(); },
    'work-order-attachments': async () => { await page.goto(`/sequfloh/record/${wo}`); await view(page).getByRole('tab', { name: 'Findings' }).click(); },
  };
  expect(Object.keys(visits).sort()).toEqual(Object.keys(UPLOAD_SURFACES).sort());
  for (const s of Object.keys(UPLOAD_SURFACES)) {
    await visits[s]();
    const box = view(page).locator(`[data-upload-surface="${s}"]`);
    await expect(box, s).toBeVisible();
    await expect(box.getByRole('button', { name: 'Import file' }), s).toBeVisible();
    await expect(box.getByRole('button', { name: 'Import folder' }), s).toBeVisible();
  }
});

test('Procedure Instructions import as file-only instructions shown in the viewer', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString().slice(-6);
  await page.goto('/sequfloh/instructions/procedure');
  await view(page).locator('input[type="file"]:not([webkitdirectory])').first().setInputFiles({ name: `Made-up procedure ${stamp}.pdf`, mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% made-up procedure\n%%EOF\n') });
  await expect(page.getByText('Each file becomes a Procedure Instruction').first()).toBeVisible();
  await page.getByTestId('bulk-import-start').click();
  await expect(page.getByTestId('bulk-import').getByText('Imported 1')).toBeVisible({ timeout: 20_000 });
  await page.goto('/sequfloh/instructions/procedure');
  await view(page).getByText(`Made-up procedure ${stamp}`).first().click({ timeout: 20_000 });
  await expect(view(page).getByTestId('procedure-file')).toBeVisible();
  await expect(view(page).getByTestId('procedure-file').locator('iframe')).toHaveCount(1);
});
