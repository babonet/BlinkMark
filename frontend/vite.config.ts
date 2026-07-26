// Imported from 'vitest/config' rather than 'vite'. Vite's own `defineConfig` does not know
// about the `test` block below, so the plain import type-checks as an unknown property.
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_ORIGIN ?? 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/unit/**/*.{test,spec}.{ts,tsx}'],
    globals: true,
    // src/config.ts fails fast on missing configuration at import time, which is the behaviour we
    // want in a browser — a misconfigured deployment should not start and silently point at the
    // wrong tenant. It does mean any test that transitively imports it needs values present.
    //
    // These are obvious placeholders on purpose. They must never look like a real tenant or a
    // real origin, because a value copied out of a test file and into a deployment is a whole
    // category of incident.
    env: {
      VITE_API_ORIGIN: 'https://api.blinkmark.invalid',
      VITE_PREVIEW_ORIGIN: 'https://preview.blinkmark.invalid',
      VITE_ENTRA_TENANT_ID: '00000000-0000-0000-0000-000000000000',
      VITE_ENTRA_CLIENT_ID: '00000000-0000-0000-0000-000000000000',
      VITE_API_SCOPE: 'api://00000000-0000-0000-0000-000000000000/access_as_user',
    },
  },
});
