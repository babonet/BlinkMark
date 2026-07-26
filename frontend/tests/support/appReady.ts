import { expect, type Page } from '@playwright/test';

/**
 * Shared preconditions for the browser suites.
 *
 * Both helpers exist because of the same failure mode: when the shell does not render at all,
 * every assertion in the suite degrades into an identical "element(s) not found" timeout, and the
 * axe scans go *green* — an empty document has no violations. A silent pass on a blank page is
 * worse than a failure, because it reports accessibility coverage that never ran.
 */

/**
 * Fails immediately, and legibly, if the application shell did not mount.
 *
 * The usual cause is a missing `VITE_*` value: `config.ts` throws while the module graph is being
 * evaluated, so nothing is ever rendered.
 */
export async function expectAppRendered(page: Page): Promise<void> {
  await expect(
    page.getByRole('link', { name: /skip to main content/i }),
    'The application shell did not render. Check that the VITE_* configuration is present — ' +
      'config.ts throws at import time when it is not, which leaves a blank document.',
  ).toBeAttached();
}

/**
 * Answers the local development token endpoint in-browser.
 *
 * The dev-auth path fetches a token from the local host before any API call. That host is a .NET
 * process which does not run in the frontend CI job, so without this stub the first failure is a
 * connection error from an origin the test never intended to exercise, masking whatever the test
 * was actually about. The token is never validated here — nothing in these tests reaches a real
 * API — so a placeholder is honest rather than a shortcut.
 */
export async function stubLocalIdentity(page: Page): Promise<void> {
  await page.route('**/dev/token*', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        accessToken: 'browser-test-token',
        userId: 'browser-test-user',
        displayName: 'Browser Test User',
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      }),
    });
  });
}
