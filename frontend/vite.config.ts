// Build configuration only. The Vitest configuration lives in vitest.config.ts.
//
// They are separate files because Vitest 2 carries its own nested copy of Vite while this project
// resolves a different top-level one. Declaring `test` here means importing `defineConfig` from
// 'vitest/config', which drags in that second Vite, and the two `Plugin` types are then
// structurally incompatible — `react()` stops being assignable to `plugins`. Keeping each config
// on its own `defineConfig` means only one Vite type is ever in play per file.
//
// The underlying duplicate is worth removing by upgrading Vitest; splitting the config stops it
// breaking the build in the meantime.
import { defineConfig } from 'vite';
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
});
