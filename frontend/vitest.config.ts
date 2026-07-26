// Vitest configuration.
//
// Separate from vite.config.ts on purpose — see the note there. In short: Vitest 2 resolves its
// own nested copy of Vite, so mixing `defineConfig` from 'vitest/config' with Vite plugins typed
// against the top-level Vite makes the two `Plugin` types structurally incompatible and breaks
// `tsc`. Keeping the test configuration in its own file means each config sees exactly one Vite.
//
// No `plugins` entry is needed here: the unit tests are plain TypeScript with no JSX, so nothing
// requires the React transform. A test that renders a component would need it, and would be the
// point at which to fix the duplicate Vite properly rather than work around it again.
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/unit/**/*.{test,spec}.{ts,tsx}'],
    globals: true,

    // src/config.ts throws on missing configuration at import time, which is the behaviour we
    // want in a browser — a misconfigured deployment should refuse to start rather than quietly
    // point at the wrong tenant. It does mean any test that transitively imports it needs values.
    //
    // These are obvious placeholders by design. They must never resemble a real tenant or a real
    // origin: a value copied out of a test file into a deployment is its own category of incident.
    env: {
      VITE_API_ORIGIN: 'https://api.blinkmark.invalid',
      VITE_PREVIEW_ORIGIN: 'https://preview.blinkmark.invalid',
      VITE_ENTRA_TENANT_ID: '00000000-0000-0000-0000-000000000000',
      VITE_ENTRA_CLIENT_ID: '00000000-0000-0000-0000-000000000000',
      VITE_API_SCOPE: 'api://00000000-0000-0000-0000-000000000000/access_as_user',
    },
  },
});
