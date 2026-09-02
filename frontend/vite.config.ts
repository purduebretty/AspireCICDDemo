import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The API resource's name in AppHost.cs. Aspire derives the injected env var names from it,
// so keep the two in sync — renaming the resource without updating this is what previously
// left the proxy target undefined and made every /api call fail with a 502.
const SERVER_RESOURCE = 'brettaspiredemo-server';

// Aspire injects the referenced resource's address under two naming schemes. Prefer the
// service-discovery vars (they carry the real deployed address, including any override the
// AppHost bakes in); fall back to the friendly {RESOURCE}_HTTPS/_HTTP form. Note the
// service-discovery keys keep the resource name verbatim — hyphen included — so they can
// only be read by index, not by property access.
const envKey = SERVER_RESOURCE.toUpperCase().replace(/-/g, '_');
const target =
  process.env[`services__${SERVER_RESOURCE}__https__0`] ||
  process.env[`services__${SERVER_RESOURCE}__http__0`] ||
  process.env[`${envKey}_HTTPS`] ||
  process.env[`${envKey}_HTTP`];

if (!target) {
  // Fail loudly instead of silently proxying to `undefined`, which Vite surfaces only as an
  // opaque 502 on every /api request. Run the frontend via the AppHost (`aspire run`), which
  // supplies these vars, rather than `npm run dev` on its own.
  throw new Error(
    `vite.config.ts: no address for the '${SERVER_RESOURCE}' resource in the environment. ` +
    `Expected one of services__${SERVER_RESOURCE}__https__0, ` +
    `services__${SERVER_RESOURCE}__http__0, ${envKey}_HTTPS or ${envKey}_HTTP. ` +
    `Start the app with 'aspire run' so the AppHost injects them.`
  );
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy API calls to the app service
      '/api': {
        target,
        changeOrigin: true
      }
    }
  }
});
