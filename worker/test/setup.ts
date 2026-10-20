import { applyD1Migrations, env } from "cloudflare:test";

declare global {
  namespace Cloudflare {
    interface Env { TEST_MIGRATIONS: import("cloudflare:test").D1Migration[]; DB: D1Database; IMAGES: ImagesBinding; MEDIA_KV_TEST: KVNamespace }
  }
}

await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
