// Playwright configuration for the offline dashboard end-to-end test (task 7, Stage B).
// The stack must already run: `docker compose up -d --wait` (port 5080) or scripts/offline_demo.py.
import { defineConfig, devices } from '@playwright/test';

const ci = Boolean(process.env.CI);

export default defineConfig({
  testDir: './specs',
  outputDir: './test-results',
  fullyParallel: false,
  workers: 1, // the specs share one stack with persistent state
  forbidOnly: ci,
  retries: 0, // no reset command: a retry would start from non-fresh state
  timeout: 180_000,
  globalTimeout: 15 * 60_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { outputFolder: './playwright-report', open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5080',
    trace: 'retain-on-failure',
    viewport: { width: 1600, height: 1000 },
    locale: 'pl-PL',
    timezoneId: 'UTC',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1600, height: 1000 } } }],
});
