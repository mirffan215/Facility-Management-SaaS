import { defineConfig, devices } from '@playwright/test';

// Full-stack tests. Run through scripts/e2e-full.sh, which starts SQL Server, the API, the web build and the fake identity provider.
export default defineConfig({
  testDir: './e2e/full',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: { baseURL: 'http://localhost:5173', actionTimeout: 10_000, navigationTimeout: 15_000, trace: 'retain-on-failure', screenshot: 'only-on-failure', ...devices['Desktop Chrome'] },
  outputDir: 'test-results',
});
