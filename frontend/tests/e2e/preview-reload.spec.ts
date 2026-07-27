import { test, expect } from '@playwright/test';
import { stubLocalIdentity } from '../support/appReady';
import { createFile } from '../support/fixtures';

/**
 * A preview reloaded after its token expired must recover silently (T128).
 *
 * This is the failure users hit by accident rather than on purpose: a laptop wakes up, a tab is
 * restored, someone navigates back. In every one of those cases the reader is still perfectly
 * authorized, and showing them an authentication error would be the product's own bookkeeping
 * leaking into their view.
 *
 * The contract is explicit that a 401 from the preview origin means "re-mint and retry", never
 * "report a problem".
 */
test.describe('Preview token renewal', () => {
  /*
   * No identity stub here.
   *
   * `stubLocalIdentity` answers /dev/token with a placeholder, which was right when these tests
   * never reached a real API. They do now — the file is created through it — and a placeholder
   * token is rejected by the real authentication handler, so the document never loads and the
   * failure points at the preview rather than at the stub that caused it.
   *
   * The local host answers /dev/token with a genuine token, so there is nothing left to stub.
   */
  test('a 401 from the preview origin is re-minted rather than surfaced', async ({ page, request }) => {
    let previewRequests = 0;
    let metadataRequests = 0;

    // The first preview fetch fails as though the token had expired. Everything after it
    // succeeds, which is what a freshly minted token would do.
    await page.route('**/p/**', async (route) => {
      previewRequests += 1;

      if (previewRequests === 1) {
        await route.fulfill({ status: 401, body: '' });
        return;
      }

      await route.fulfill({
        status: 200,
        contentType: 'text/html',
        body: '<h1>Rendered preview</h1>',
      });
    });

    await page.route('**/api/files/*', async (route) => {
      metadataRequests += 1;
      await route.continue();
    });

    await page.goto(`/files/${await createFile(request)}`);

    // Wait for the document itself before reaching for the disclosure. The route interception
    // above delays the metadata request, and clicking at a fixed moment races it.
    await expect(page.locator('.passage').first()).toBeVisible();

    // The rendered preview is behind a disclosure; the document itself is what a reader lands on.
    await page
      .locator('summary')
      .filter({ hasText: /see exactly how this file renders/i })
      .click();

    // The reader sees content, and never an authentication failure.
    await expect(page.getByRole('group', { name: /preview of/i })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);

    // The metadata endpoint was called again to obtain a fresh token.
    expect(metadataRequests).toBeGreaterThan(1);
  });

  test('a genuinely unavailable file reports plainly instead of retrying forever', async ({ page }) => {
    // This one never reaches the API — every response is stubbed — so the identity stub is still
    // the right tool. Without it the page would fetch a token from a host this test does not
    // otherwise need.
    await stubLocalIdentity(page);

    await page.route('**/api/files/*', async (route) => {
      await route.fulfill({
        status: 404,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 404, title: 'Not found.' }),
      });
    });

    await page.goto('/files/01JBQZK4T3N7XW9E2M6H0YV8QD');

    // Expired and never-existed are indistinguishable on purpose, so the message covers both
    // without claiming to know which one happened.
    await expect(page.getByRole('alert')).toContainText(/expired|not available/i);
  });
});
