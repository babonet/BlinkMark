/**
 * Runtime configuration.
 *
 * Every value here is an identifier or an origin. There is no client secret, because a
 * single-page application cannot keep one — it runs on the user's machine, and anything shipped
 * to it is public. Authentication is authorization-code flow with PKCE for exactly that reason.
 */
export interface AppConfig {
  apiOrigin: string;
  previewOrigin: string;
  tenantId: string;
  clientId: string;
  apiScopes: string[];
}

function required(value: string | undefined, name: string): string {
  if (!value) {
    throw new Error(
      `Missing configuration '${name}'. Set it in .env.local for development, or in the deploy workflow for a deployed environment.`,
    );
  }
  return value;
}

export const config: AppConfig = {
  apiOrigin: import.meta.env.VITE_API_ORIGIN ?? window.location.origin,
  previewOrigin: required(import.meta.env.VITE_PREVIEW_ORIGIN, 'VITE_PREVIEW_ORIGIN'),
  tenantId: required(import.meta.env.VITE_ENTRA_TENANT_ID, 'VITE_ENTRA_TENANT_ID'),
  clientId: required(import.meta.env.VITE_ENTRA_CLIENT_ID, 'VITE_ENTRA_CLIENT_ID'),
  apiScopes: (import.meta.env.VITE_API_SCOPE ?? '').split(' ').filter(Boolean),
};
