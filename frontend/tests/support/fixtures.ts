import type { APIRequestContext } from '@playwright/test';

/**
 * Test data helpers.
 *
 * Every test that needs a document creates its own, rather than reusing whichever file happens to
 * be first in the list. That is not tidiness for its own sake — the shared-file version failed in
 * three separate ways within one afternoon:
 *
 *   - assertions on fixed comment text hit strict-mode violations once a second run had added a
 *     duplicate;
 *   - a `.first()` reply button pointed at a thread from a previous run;
 *   - and eventually axe timed out at thirty seconds, because runs had piled enough comments onto
 *     one file to make the page too large to analyse.
 *
 * All three looked like product bugs and none of them were. Tests that share mutable state do not
 * fail honestly.
 */

/** The local development host. These helpers are for local and CI runs against it. */
const apiOrigin = process.env.PLAYWRIGHT_API_ORIGIN ?? 'http://localhost:5080';

export const SAMPLE_DOCUMENT = `# Ingest pipeline review

A first pass at the redesign. Comments welcome on anything.

## Background

The current pipeline was written for a single tenant and has been extended twice since.

> We measured p95 at 1.4 seconds during the March incident.

## Proposed changes

- Move parsing off the request thread
- Batch writes in windows of 200ms
- Drop the intermediate representation

## Open questions

1. Do we need the intermediate representation for replay?
2. Who owns the migration?
`;

/**
 * Obtains an access token from the local host.
 *
 * Only the local development host exposes this; there is no such endpoint in a deployed
 * environment, and there must never be. The token itself is a genuine JWT that the production
 * authentication handler validates in full — see backend/tools/BlinkMark.LocalHost.
 */
export async function devToken(request: APIRequestContext, user = 'alice'): Promise<string> {
  const response = await request.get(`${apiOrigin}/dev/token?user=${encodeURIComponent(user)}`);

  if (!response.ok()) {
    throw new Error(
      `The local development host is not answering on ${apiOrigin}. ` +
        'Start it with: dotnet run --project backend/tools/BlinkMark.LocalHost',
    );
  }

  return (await response.json()).accessToken as string;
}

/** Uploads a document and returns its id. */
export async function createFile(
  request: APIRequestContext,
  markdown: string = SAMPLE_DOCUMENT,
  user = 'alice',
): Promise<string> {
  const token = await devToken(request, user);

  const response = await request.post(`${apiOrigin}/api/files`, {
    headers: { Authorization: `Bearer ${token}` },
    multipart: {
      file: {
        // Unique per upload, so a failure report names the run that produced it.
        name: `review-${crypto.randomUUID().slice(0, 8)}.md`,
        mimeType: 'text/markdown',
        buffer: Buffer.from(markdown, 'utf8'),
      },
    },
  });

  if (!response.ok()) {
    throw new Error(`Could not create a test file: ${response.status()} ${await response.text()}`);
  }

  return (await response.json()).id as string;
}
