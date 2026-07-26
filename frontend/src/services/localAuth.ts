import { config } from '../config';

/**
 * Local development sign-in.
 *
 * Stands in for MSAL, and only for MSAL. The token this fetches is a genuine JWT minted by the
 * local host, and the API validates it with the production authentication handler — issuer,
 * audience, lifetime, signature, tenant, and scope all still checked. Nothing downstream behaves
 * differently because the token came from here.
 *
 * Enabled only when `VITE_LOCAL_DEV_AUTH` is set *and* Vite is running a development build. Both
 * conditions are required: the flag alone could be set by accident in a deployment pipeline,
 * while `import.meta.env.DEV` is fixed at build time and is false in anything `vite build`
 * produces. A production bundle therefore cannot take this path however it is configured, and
 * `assertLocalAuthUnreachable` below fails loudly if that assumption is ever broken.
 */
export const isLocalDevAuth = import.meta.env.DEV && import.meta.env.VITE_LOCAL_DEV_AUTH === 'true';

/** Who you are signed in as locally. Change it to test another person's view of a file. */
const localUser = import.meta.env.VITE_LOCAL_DEV_USER ?? 'local-dev-user';

interface LocalToken {
  accessToken: string;
  userId: string;
  displayName: string;
  expiresAt: string;
}

let cached: LocalToken | null = null;

export async function acquireLocalAccessToken(): Promise<string> {
  assertLocalAuthUnreachable();

  // Re-fetch a minute before expiry rather than on failure, so a long session does not produce a
  // confusing 401 in the middle of someone's work.
  if (cached && new Date(cached.expiresAt).getTime() - Date.now() > 60_000) {
    return cached.accessToken;
  }

  const response = await fetch(`${config.apiOrigin}/dev/token?user=${encodeURIComponent(localUser)}`);

  if (!response.ok) {
    throw new Error(
      `The local development host is not answering on ${config.apiOrigin}. ` +
        'Start it with: dotnet run --project backend/tools/BlinkMark.LocalHost',
    );
  }

  cached = (await response.json()) as LocalToken;
  return cached.accessToken;
}

export function localAccount() {
  // Falls back to a name derived from the configured user rather than a generic one. Showing
  // "Local Developer" in the header while a file says "Uploaded by Alice" is exactly the sort of
  // small inconsistency that sends someone hunting for an identity bug that does not exist.
  return {
    name: cached?.displayName ?? toDisplayName(localUser),
    username: `${localUser}@localhost`,
    localAccountId: cached?.userId ?? localUser,
    idTokenClaims: { oid: cached?.userId ?? localUser },
  };
}

/** Mirrors the local host's own formatting, so the two agree before the first token arrives. */
function toDisplayName(userId: string): string {
  return userId
    .replace(/[-.]/g, ' ')
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => word[0].toUpperCase() + word.slice(1))
    .join(' ');
}

/**
 * Refuses to run outside a development build.
 *
 * A belt-and-braces check against the one failure that would matter: a production bundle that
 * somehow reached this code and started asking an arbitrary origin for credentials.
 */
function assertLocalAuthUnreachable() {
  if (!import.meta.env.DEV) {
    throw new Error(
      'Local development authentication was reached in a production build. This is a bug, and a ' +
        'serious one — refusing to continue.',
    );
  }
}
