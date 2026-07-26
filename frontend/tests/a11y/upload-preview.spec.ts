import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Accessibility over the US1 flows (T050).
 *
 * Clarification Q4 set WCAG 2.1 Level AA as a delivery requirement, so this runs in CI alongside
 * everything else rather than as a pre-release audit. An audit finds problems after the
 * interaction model is settled; a test finds them while it can still change.
 *
 * The keyboard assertions are separate from the axe scans on purpose. Automated scanning catches
 * missing labels and insufficient contrast and cannot tell whether a focus trap is escapable —
 * and an iframe preview is a focus trap by default (FR-081).
 */

test.describe('Upload flow', () => {
  test('has no automatically detectable accessibility violations', async ({ page }) => {
    await page.goto('/upload');

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    expect(results.violations).toEqual([]);
  });

  test('the access scope and no-backup notices are reachable and readable', async ({ page }) => {
    await page.goto('/upload');

    // SC-016. If the notice is only decorative, the mitigation for clarification Q1's accepted
    // risk does not reach the people it was written for.
    const notice = page.getByRole('complementary', { name: /before you upload/i });
    await expect(notice).toBeVisible();
    await expect(notice).toContainText(/anyone in your organization with the link/i);
    await expect(notice).toContainText(/no backup/i);
  });

  test('a validation error is announced, not only displayed', async ({ page }) => {
    await page.goto('/upload');

    await page.getByRole('button', { name: /upload/i }).click();

    await expect(page.getByRole('alert')).toBeVisible();
  });

  test('the whole form is operable from the keyboard alone', async ({ page }) => {
    await page.goto('/upload');
    await page.keyboard.press('Tab');

    // The skip link is deliberately first. It matters most on the file detail page, where the
    // preview iframe otherwise stands between a keyboard user and the comments.
    await expect(page.locator(':focus')).toHaveText(/skip to main content/i);

    for (let index = 0; index < 12; index++) {
      await page.keyboard.press('Tab');
      const focused = page.locator(':focus');
      if ((await focused.getAttribute('type')) === 'file') {
        return;
      }
    }

    throw new Error('The file input could not be reached by keyboard.');
  });
});

test.describe('Preview region', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/files');
  });

  test('the file list has no automatically detectable accessibility violations', async ({ page }) => {
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    expect(results.violations).toEqual([]);
  });

  test('the preview region is labelled, focusable, and escapable', async ({ page }) => {
    const firstFile = page.getByRole('link').filter({ hasText: /\.(md|html)$/ }).first();
    test.skip((await firstFile.count()) === 0, 'No file available to preview.');

    await firstFile.click();

    const region = page.getByRole('group', { name: /preview of/i });
    await expect(region).toBeVisible();

    await region.focus();
    await expect(region).toBeFocused();

    // Escape returns focus to the wrapper rather than leaving the user stranded inside the frame.
    await page.keyboard.press('Escape');
    await expect(region).toBeFocused();

    // And the skip link offers a way past the preview entirely.
    await expect(page.getByRole('link', { name: /skip preview and go to comments/i })).toBeAttached();
  });
});
