import { createExecutionContext, env, waitOnExecutionContext } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import worker from "../src/index";

type Env = typeof env & Record<string, unknown>;
let counter = 0;
const uniq = (s: string) => `${s}${++counter}${Math.random().toString(36).slice(2, 6)}`;

async function call(method: string, path: string, o: { token?: string; json?: unknown; raw?: string } = {}) {
  const headers: Record<string, string> = {};
  if (o.token) headers.authorization = `Bearer ${o.token}`;
  let body: BodyInit | undefined;
  if (o.json !== undefined) { headers["content-type"] = "application/json"; body = JSON.stringify(o.json); }
  if (o.raw !== undefined) { headers["content-type"] = "application/json"; body = o.raw; }
  const ctx = createExecutionContext();
  const resp = await worker.fetch(new Request(`http://localhost${path}`, { method, headers, body }), { ...(env as Env) } as never, ctx);
  await waitOnExecutionContext(ctx);
  return resp;
}
const j = async (r: Response) => (await r.json()) as Record<string, any>;
async function signIn() {
  const r = await call("POST", "/api/community/auth/dev", { json: { login: uniq("tester"), accountAgeDays: 365 } });
  return (await j(r)).token as string;
}
const hex = (n: number) => n.toString(16).padStart(32, "0");

function result(i: number, extra: Record<string, unknown> = {}) {
  return {
    v: 1, key: hex(1_000_000 + i + counter * 1000), tweak: "gen.menu-delay", kind: "synthetic", game: null,
    cpu: "AMD Ryzen 5 7600X", gpu: "NVIDIA GeForce RTX 4070 SUPER", ramGb: 32, osBuild: 26100, app: "1.54.0",
    metrics: [{ k: "fps", pct: 2 + (i % 3), verdict: i % 4 === 0 ? "same" : "better", nBefore: 2, nAfter: 2 }],
    ...extra,
  };
}

describe("Tweak Truth Index: shared evidence", () => {
  it("needs sign-in to share, but the stored row has no user, login or time of day", async () => {
    expect((await call("POST", "/api/community/evidence", { json: result(1) })).status).toBe(401);
    const token = await signIn();
    const r = await call("POST", "/api/community/evidence", { token, json: { ...result(2), login: "someone", hostname: "MY-PC", ip: "1.2.3.4" } });
    expect(r.status).toBe(200);
    expect((await j(r)).accepted).toBe(true);
    const row = await env.DB.prepare("SELECT * FROM evidence WHERE key = ?1").bind(result(2).key).first<Record<string, unknown>>();
    expect(Object.keys(row!).sort()).toEqual(["app", "cpu", "day", "game", "gpu", "key", "kind", "metrics", "os_build", "ram_gb", "tweak"].sort());
    expect(JSON.stringify(row)).not.toMatch(/someone|MY-PC|1\.2\.3\.4|tester/);
    expect(row!.day).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it("rejects anything outside the whitelist", async () => {
    const token = await signIn();
    const bad: Record<string, unknown>[] = [
      { cpu: "C:\\Users\\antti" }, { gpu: "<script>" }, { ramGb: 31 }, { osBuild: 5 }, { kind: "made-up" }, { game: "Not a game" },
      { key: "XYZ" }, { tweak: "../etc" }, { metrics: [] }, { metrics: [{ k: "fps", pct: 999, verdict: "better", nBefore: 2, nAfter: 2 }] },
      { metrics: [{ k: "fps", pct: 1, verdict: "better", nBefore: 1, nAfter: 2 }] }, { metrics: [{ k: "nope", pct: 1, verdict: "better", nBefore: 2, nAfter: 2 }] },
      { v: 2 },
    ];
    for (const b of bad) {
      const r = await call("POST", "/api/community/evidence", { token, json: result(3, b) });
      expect(r.status, JSON.stringify(b)).toBe(400);
      expect((await j(r)).problems.length).toBeGreaterThan(0);
    }
  });

  it("counts the same test only once", async () => {
    const token = await signIn();
    const body = result(4);
    expect((await j(await call("POST", "/api/community/evidence", { token, json: body }))).accepted).toBe(true);
    const again = await j(await call("POST", "/api/community/evidence", { token, json: body }));
    expect(again.accepted).toBe(false);
    expect(again.duplicate).toBe(true);
  });

  it("publishes nothing for a group before five submissions, then robust aggregates", async () => {
    const tweak = "adv." + uniq("t").toLowerCase();
    const token = await signIn();
    for (let i = 0; i < 4; i++) await call("POST", "/api/community/evidence", { token, json: result(100 + i, { tweak }) });
    let s = await j(await call("GET", `/api/community/evidence/${tweak}`));
    const all = s.groups.find((g: any) => g.dimension === "all");
    expect(all.submissions).toBe(4);
    expect(all.metrics).toEqual([]); // not enough evidence: no numbers at all
    // One wild outlier must not move the median much.
    await call("POST", "/api/community/evidence", { token, json: result(104, { tweak, metrics: [{ k: "fps", pct: 250, verdict: "better", nBefore: 2, nAfter: 2 }] }) });
    s = await j(await call("GET", `/api/community/evidence/${tweak}`));
    const fps = s.groups.find((g: any) => g.dimension === "all").metrics.find((m: any) => m.k === "fps");
    expect(fps.n).toBe(5);
    expect(fps.medianPct).toBeLessThan(5);
    expect(fps.betterShare + fps.worseShare).toBeLessThanOrEqual(1);
    expect(s.groups.some((g: any) => g.dimension === "gpu" && g.value === "NVIDIA GeForce RTX 4070 SUPER")).toBe(true);
    expect(s.groups.some((g: any) => g.dimension === "os" && g.value === "Windows 11")).toBe(true);
  });

  it("limits shares per account per day", async () => {
    const token = await signIn();
    let last: Response | null = null;
    for (let i = 0; i < 51; i++) last = await call("POST", "/api/community/evidence", { token, json: result(1000 + i, { tweak: "gen.quota-test" }) });
    expect(last!.status).toBe(429);
  });

  it("works on an empty tweak and validates the id", async () => {
    const s = await j(await call("GET", "/api/community/evidence/gen.nothing-here"));
    expect(s.groups).toEqual([]);
    expect((await call("GET", "/api/community/evidence/..%2Fx")).status).toBe(400);
  });
});
