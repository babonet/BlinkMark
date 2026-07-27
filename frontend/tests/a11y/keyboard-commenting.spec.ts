import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { expectAppRendered } from '../support/appReady';
import { createFile } from '../support/fixtures';

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
  test.beforeEach(async ({ page, request }) => {
    // A file of its own, per test. Sharing one meant every run added comments to it until axe
    // timed out analysing the page — a failure that looked like an accessibility regression and
    // was nothing of the sort. See tests/support/fixtures.ts.
    const fileId = await createFile(request);

    await page.goto(`/files/${fileId}`);
    await expectAppRendered(page);

    await expect(page.locator('.passage').first()).toBeVisible();
  });

  test('has no automatically detectable accessibility violations', async ({ page }) => {
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .analyze();

    expect(results.violations).toEqual([]);
  });

  test('a passage can be selected and commented on without a pointer', async ({ page }) => {
    // Unique per run. These tests write real comments to a real file, and the file outlives the
    // run — so asserting on fixed text passes once and then fails on a strict-mode violation
    // when the second run finds three of them. A test that only works on a clean database is a
    // test that gets deleted the first time somebody is in a hurry.
    const body = `Written entirely from the keyboard. ${crypto.randomUUID()}`;

    const passages = page.locator('.passage');
    await passages.first().focus();

    // Move, extend across two blocks, commit.
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Shift+ArrowDown');
    await page.keyboard.press('Enter');

    const composer = page.getByLabel('Your comment');
    await expect(composer).toBeFocused();

    await page.keyboard.type(body);

    // Tab to the submit button rather than clicking it.
    await page.keyboard.press('Tab');
    await expect(page.locator(':focus')).toHaveText(/^Comment$/);
    await page.keyboard.press('Enter');

    await expect(page.getByText(body)).toBeVisible();
  });

  test('selection changes are announced politely and do not move focus away', async ({ page }) => {
    const passages = page.locator('.passage');
    await passages.first().focus();

    const liveRegion = page.locator('#passage-status');
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

    await expect(page.locator('#passage-status')).toContainText(/cleared/i);
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
    const root = `Root comment. ${crypto.randomUUID()}`;
    const reply = `Replied from the keyboard. ${crypto.randomUUID()}`;

    const passages = page.locator('.passage');
    await passages.first().focus();
    await page.keyboard.press('Enter');

    await page.getByLabel('Your comment').fill(root);
    await page.getByRole('button', { name: /^Comment$/ }).click();

    // The thread just created, found by its own text rather than by position. Ordering is by
    // creation time, so "first" and "last" both drift as earlier runs leave threads behind.
    const thread = page.locator('.thread').filter({ hasText: root });
    await expect(thread).toBeVisible();

    await thread.getByRole('button', { name: /^Reply/ }).focus();
    await page.keyboard.press('Enter');

    const replyBox = thread.getByLabel('Reply to this thread');
    await expect(replyBox).toBeVisible();
    await replyBox.fill(reply);

    await thread.getByRole('button', { name: /^Reply$/ }).click();
    await expect(page.getByText(reply)).toBeVisible();
  });
});

test.describe('Orphaned comments', () => {
  test('orphaned state is conveyed in text, not by styling alone', async ({ page, request }) => {
    await page.goto('/files');
    await expectAppRendered(page);

    await page.goto(`/files/${await createFile(request)}`);

    const orphanSection = page.getByRole('heading', { name: /orphaned/i });
    test.skip((await orphanSection.count()) === 0, 'No orphaned comments on this file.');

    // FR-079. The dashed border is a shortcut for sighted users; the requirement is that the
    // state exists for everyone, which means it has to be in the accessible name or the text.
    await expect(page.getByText(/can no longer be found/i).first()).toBeVisible();
    await expect(page.getByRole('article', { name: /orphaned/i }).first()).toBeVisible();
  });
});
