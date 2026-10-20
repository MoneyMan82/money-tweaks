import path from "node:path";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// Tests run inside the real Workers runtime (workerd) with local D1, R2 and Images bindings from ./wrangler.jsonc.
// Nothing touches a Cloudflare account. Test-only settings below; production values come from wrangler.jsonc + secrets.
export default defineConfig(async () => {
  const migrations = await readD1Migrations(path.join(import.meta.dirname, "migrations"));
  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: "./wrangler.jsonc" },
        miniflare: {
          kvNamespaces: ["MEDIA_KV"],
          bindings: {
            TEST_MIGRATIONS: migrations,
            DEV_SIGN_IN: "true",
            GITHUB_CLIENT_ID: "Iv1.testclient",
            GITHUB_CLIENT_SECRET: "test-secret",
            ADMIN_GITHUB_IDS: "4242",
            IMAGE_REVIEW: "post",
          },
        },
      }),
    ],
    test: { setupFiles: ["./test/setup.ts"] },
  };
});
