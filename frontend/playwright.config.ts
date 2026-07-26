import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end and accessibility tests.
 *
 * Accessibility runs here rather than in a separate suite because WCAG 2.1 AA (clarification Q4)
 * is a delivery requirement, and a requirement checked in an optional job is a requirement that
 * eventually stops being checked.
 *
 * The keyboard-only project exists because FR-078 and FR-082 mean the commenting interaction
 * model cannot be pointer-first. Running the same flows with no pointer at all is the only way to
 * find out whether that actually holds.
 */
export default defineConfig({
  testDir: './tests',
  testMatch: ['**/e2e/**/*.spec.ts', '**/a11y/**/*.spec.ts'],
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['html'], ['github']] : [['list']],

  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'keyboard-only',
      use: {
        ...devices['Desktop Chrome'],
        hasTouch: false,
      },
      testMatch: ['**/a11y/**/*.spec.ts'],
    },
  ],

  webServer: process.env.PLAYWRIGHT_BASE_URL
    ? undefined
    : {
        command: 'npm run dev',
        url: 'http://localhost:5173',
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
      },
});
