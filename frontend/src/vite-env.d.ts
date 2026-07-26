/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_ORIGIN?: string;
  readonly VITE_PREVIEW_ORIGIN?: string;
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  readonly VITE_API_SCOPE?: string;

  /** Local development only. See services/localAuth.ts. */
  readonly VITE_LOCAL_DEV_AUTH?: string;
  readonly VITE_LOCAL_DEV_USER?: string;

  /** Set by Vite. False in anything `vite build` produces. */
  readonly DEV: boolean;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
