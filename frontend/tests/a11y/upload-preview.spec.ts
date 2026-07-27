import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { expectAppRendered } from '../support/appReady';
import { createFile } from '../support/fixtures';

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
    await expectAppRendered(page);

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

    // Before any skip decision below. Without this the file-count check runs while the list is
    // still loading, sees zero, and skips — which reads in the output as "no data available"
    // rather than "this test never actually ran". A suite that quietly tests nothing is worse
    // than a red one.
    await expectAppRendered(page);
  });

  test('the file list has no automatically detectable accessibility violations', async ({ page }) => {
    await expectAppRendered(page);

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    expect(results.violations).toEqual([]);
  });

  test('the preview region is labelled and focusable', async ({ page, request }) => {
    await page.goto(`/files/${await createFile(request)}`);
    await expectAppRendered(page);

    // The rendered preview is behind a disclosure, not shown by default. It is a sandboxed
    // cross-origin frame that cannot be selected or commented on, so leading with it would put
    // the one unusable surface first.
    await page
      .locator('summary')
      .filter({ hasText: /see exactly how this file renders/i })
      .click();

    const region = page.getByRole('group', { name: /preview of/i });
    await expect(region).toBeVisible();
  });

  test('the document itself is what a reader lands on', async ({ page, request }) => {
    await page.goto(`/files/${await createFile(request)}`);
    await expectAppRendered(page);

    // The regression this guards against is showing the reader an implementation artifact — the
    // flat text projection — or the one surface they cannot work in.
    await expect(page.locator('.passage').first()).toBeVisible();
    await expect(page.getByRole('group', { name: /preview of/i })).toHaveCount(0);
  });

  test('the preview can be skipped entirely', async ({ page, request }) => {
    await page.goto(`/files/${await createFile(request)}`);
    await expectAppRendered(page);
    await page
      .locator('summary')
      .filter({ hasText: /see exactly how this file renders/i })
      .click();

    // FR-081. An iframe sits in the tab order, so there has to be a way past it.
    await expect(page.getByRole('link', { name: /skip preview and go to comments/i })).toBeAttached();
  });
});
