import { test, expect } from '@playwright/test';

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
  test('a 401 from the preview origin is re-minted rather than surfaced', async ({ page }) => {
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

    await page.goto('/files');

    const firstFile = page.getByRole('link').filter({ hasText: /\.(md|html)$/ }).first();
    test.skip((await firstFile.count()) === 0, 'No file available to preview.');

    await firstFile.click();

    // The reader sees content, and never an authentication failure.
    await expect(page.getByRole('group', { name: /preview of/i })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);

    // The metadata endpoint was called again to obtain a fresh token.
    expect(metadataRequests).toBeGreaterThan(1);
  });

  test('a genuinely unavailable file reports plainly instead of retrying forever', async ({ page }) => {
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
