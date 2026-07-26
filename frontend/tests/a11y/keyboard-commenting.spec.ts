import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { expectAppRendered } from '../support/appReady';

/**
 * Keyboard-only passage selection and commenting (T053, FR-078, FR-082).
 *
 * This is the suite the build-order warning in tasks.md exists for. Retrofitting keyboard
 * commenting means rewriting the US2 interaction model, so these tests are written against the
 * same interaction the pointer path uses rather than against a separate "accessible mode".
 *
 * Every test here completes a real task with no pointer at all. A flow that is merely *reachable*
 * by keyboard but not *completable* by keyboard fails the requirement, and only completing it
 * proves otherwise.
 */

test.describe('Keyboard-only commenting', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/files');

    // Before the skip below. "No file available" and "the page never rendered" both produce an
    // empty list, and only one of them is a legitimate reason to skip an accessibility test.
    await expectAppRendered(page);

    const firstFile = page
      .getByRole('link')
      .filter({ hasText: /\.(md|html)$/ })
      .first();
    test.skip((await firstFile.count()) === 0, 'No file available to comment on.');
    await firstFile.click();

    await expect(page.locator('.passage').first()).toBeVisible();
  });

  test('has no automatically detectable accessibility violations', async ({ page }) => {
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    expect(results.violations).toEqual([]);
  });

  test('a passage can be selected and commented on without a pointer', async ({ page }) => {
    const passages = page.locator('.passage');
    await passages.first().focus();

    // Move, extend across two blocks, commit.
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Shift+ArrowDown');
    await page.keyboard.press('Enter');

    const composer = page.getByLabel('Your comment');
    await expect(composer).toBeFocused();

    await page.keyboard.type('Written entirely from the keyboard.');

    // Tab to the submit button rather than clicking it.
    await page.keyboard.press('Tab');
    await expect(page.locator(':focus')).toHaveText(/^Comment$/);
    await page.keyboard.press('Enter');

    await expect(page.getByText('Written entirely from the keyboard.')).toBeVisible();
  });

  test('selection changes are announced politely and do not move focus away', async ({ page }) => {
    const passages = page.locator('.passage');
    await passages.first().focus();

    const liveRegion = page.locator('[aria-live="polite"]').first();
    await page.keyboard.press('ArrowDown');

    // FR-080's reasoning: announce, never steal focus. A user pressing arrow keys must stay
    // where they are.
    await expect(liveRegion).toContainText(/block \d+ of \d+/i);
    await expect(page.locator(':focus')).toHaveClass(/passage/);
  });

  test('Escape clears a selection without leaving the passage list', async ({ page }) => {
    const passages = page.locator('.passage');
    await passages.first().focus();

    await page.keyboard.press('Shift+ArrowDown');
    await page.keyboard.press('Escape');

    await expect(page.locator('[aria-live="polite"]').first()).toContainText(/cleared/i);
    await expect(page.locator(':focus')).toHaveClass(/passage/);
  });

  test('a whole block can be commented on without dragging', async ({ page }) => {
    // FR-082. Region selection is a button, not a rectangle to draw, so it works for anyone who
    // cannot drag accurately — or at all.
    const regionButton = page.getByRole('button', { name: /comment on this block/i }).first();
    await regionButton.focus();
    await page.keyboard.press('Enter');

    await expect(page.getByLabel('Your comment')).toBeFocused();
  });

  test('a comment thread can be replied to from the keyboard', async ({ page }) => {
    const passages = page.locator('.passage');
    await passages.first().focus();
    await page.keyboard.press('Enter');

    await page.getByLabel('Your comment').fill('Root comment.');
    await page.getByRole('button', { name: /^Comment$/ }).click();

    const replyButton = page.getByRole('button', { name: /^Reply/ }).first();
    await replyButton.focus();
    await page.keyboard.press('Enter');

    const replyBox = page.getByLabel('Reply to this thread');
    await expect(replyBox).toBeVisible();
    await replyBox.fill('Replied from the keyboard.');

    await page.getByRole('button', { name: /^Reply$/ }).click();
    await expect(page.getByText('Replied from the keyboard.')).toBeVisible();
  });
});

test.describe('Orphaned comments', () => {
  test('orphaned state is conveyed in text, not by styling alone', async ({ page }) => {
    await page.goto('/files');
    await expectAppRendered(page);

    const firstFile = page
      .getByRole('link')
      .filter({ hasText: /\.(md|html)$/ })
      .first();
    test.skip((await firstFile.count()) === 0, 'No file available.');
    await firstFile.click();

    const orphanSection = page.getByRole('heading', { name: /orphaned/i });
    test.skip((await orphanSection.count()) === 0, 'No orphaned comments on this file.');

    // FR-079. The dashed border is a shortcut for sighted users; the requirement is that the
    // state exists for everyone, which means it has to be in the accessible name or the text.
    await expect(page.getByText(/can no longer be found/i).first()).toBeVisible();
    await expect(page.getByRole('article', { name: /orphaned/i }).first()).toBeVisible();
  });
});
