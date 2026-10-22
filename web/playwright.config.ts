import { defineConfig } from '@playwright/test';

/** Runs against a local copy of the app (dotnet run with a local database). Never point it at the live site. */
export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5080',
    viewport: { width: 1440, height: 900 },
    trace: 'retain-on-failure',
    launchOptions: process.env.E2E_CHROME ? { executablePath: process.env.E2E_CHROME, args: ['--no-sandbox'] } : {},
  },
});
