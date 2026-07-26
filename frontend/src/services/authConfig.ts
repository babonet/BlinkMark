import { Configuration, LogLevel, PublicClientApplication } from '@azure/msal-browser';
import { config } from '../config';
import { acquireLocalAccessToken, isLocalDevAuth } from './localAuth';

/**
 * MSAL configuration.
 *
 * Two choices below are load-bearing rather than defaults.
 *
 * The authority names a single tenant, not `common` or `organizations`. Principle I requires
 * membership of the owning organization, and pinning the authority means a user from another
 * tenant cannot even complete sign-in — the refusal happens at the identity provider rather
 * than in application code that someone could forget to write.
 *
 * Tokens live in session storage rather than local storage. Session storage is cleared when the
 * tab closes and is not shared across tabs, which suits a product whose whole premise is that
 * things do not stick around.
 */
const msalConfiguration: Configuration = {
  auth: {
    clientId: config.clientId,
    authority: `https://login.microsoftonline.com/${config.tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
    navigateToLoginRequestUrl: true,
  },
  cache: {
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
  system: {
    loggerOptions: {
      logLevel: LogLevel.Warning,
      // Personal data never reaches the console. FR-045 is about server logs, but the same
      // reasoning applies to anything a support screenshot might capture.
      piiLoggingEnabled: false,
      loggerCallback: (level, message, containsPii) => {
        if (containsPii) {
          return;
        }
        if (level === LogLevel.Error) {
          console.error(message);
        }
      },
    },
  },
};

export const msalInstance = new PublicClientApplication(msalConfiguration);

export const loginRequest = {
  scopes: config.apiScopes.length > 0 ? config.apiScopes : ['User.Read'],
};

/**
 * Acquires an access token for the API, falling back to an interactive prompt only when the
 * silent path genuinely cannot succeed.
 */
export async function acquireAccessToken(): Promise<string> {
  // Local development short-circuits to the local host's token endpoint. The token it returns is
  // a real JWT and is validated by the real handler; only the issuer differs. See localAuth.ts
  // for why this cannot be reached from a production build.
  if (isLocalDevAuth) {
    return acquireLocalAccessToken();
  }

  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0];

  if (!account) {
    await msalInstance.loginRedirect(loginRequest);
    throw new Error('Redirecting to sign in.');
  }

  try {
    const result = await msalInstance.acquireTokenSilent({ ...loginRequest, account });
    return result.accessToken;
  } catch {
    const result = await msalInstance.acquireTokenPopup({ ...loginRequest, account });
    return result.accessToken;
  }
}
