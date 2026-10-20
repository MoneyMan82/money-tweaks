// Bindings, configuration and small shared helpers.

export interface Env {
  ASSETS: Fetcher;
  DB?: D1Database;
  MEDIA?: R2Bucket;
  MEDIA_KV?: KVNamespace;
  IMAGES?: ImagesBinding;
  GITHUB_CLIENT_ID?: string;
  /** Secret: set with `npx wrangler secret put GITHUB_CLIENT_SECRET`. Never in wrangler.jsonc or the repository. */
  GITHUB_CLIENT_SECRET?: string;
  ADMIN_GITHUB_IDS?: string;
  /** Local development only (worker/.dev.vars). Refused for any host that isn't localhost/127.0.0.1. */
  DEV_SIGN_IN?: string;
  DAILY_UPLOAD_LIMIT?: string;
  MAX_UPLOAD_BYTES?: string;
  MAX_IMAGE_DIMENSION?: string;
  MIN_IMAGE_DIMENSION?: string;
  IMAGE_REVIEW?: string;
  MIN_ACCOUNT_AGE_DAYS?: string;
  POSTS_PER_DAY?: string;
  REPORTS_PER_DAY?: string;
  REPORT_HIDE_THRESHOLD?: string;
  MAX_TOTAL_STORAGE_BYTES?: string;
}

export interface Limits {
  dailyUploads: number; maxUploadBytes: number; maxDimension: number; minDimension: number; thumbSize: number;
  imageReview: "pre" | "post"; minAccountAgeDays: number; postsPerDay: number; reportsPerDay: number; reportHideThreshold: number;
  maxTotalStorageBytes: number; reservationMinutes: number; orphanHours: number; removedRetentionDays: number; sessionDays: number;
}

const n = (v: string | undefined, d: number, lo: number, hi: number) => {
  const x = Number(v);
  return v !== undefined && v !== "" && Number.isFinite(x) ? Math.min(hi, Math.max(lo, Math.floor(x))) : d;
};

export function limits(env: Env): Limits {
  return {
    dailyUploads: n(env.DAILY_UPLOAD_LIMIT, 2, 0, 100),
    maxUploadBytes: n(env.MAX_UPLOAD_BYTES, 5 * 1048576, 1024, 20 * 1048576),
    maxDimension: n(env.MAX_IMAGE_DIMENSION, 4096, 64, 8192),
    minDimension: n(env.MIN_IMAGE_DIMENSION, 16, 1, 512),
    thumbSize: 480,
    imageReview: env.IMAGE_REVIEW === "post" ? "post" : "pre",
    minAccountAgeDays: n(env.MIN_ACCOUNT_AGE_DAYS, 7, 0, 365),
    postsPerDay: n(env.POSTS_PER_DAY, 5, 1, 1000),
    reportsPerDay: n(env.REPORTS_PER_DAY, 20, 1, 1000),
    reportHideThreshold: n(env.REPORT_HIDE_THRESHOLD, 3, 1, 100),
    maxTotalStorageBytes: n(env.MAX_TOTAL_STORAGE_BYTES, 9e9, 1e6, 1e13),
    reservationMinutes: 10,
    orphanHours: 24,
    removedRetentionDays: 30,
    sessionDays: 30,
  };
}

/** An expected failure with a status and a message that's safe to show users. */
export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public extra?: Record<string, unknown>) { super(message); }
}

// The clock can be moved by tests (UTC-midnight quota reset, cleanup ages).
let clockOffsetMs = 0;
export const now = () => new Date(Date.now() + clockOffsetMs);
export const setClockForTests = (d: Date | null) => { clockOffsetMs = d ? d.getTime() - Date.now() : 0; };
export const iso = (d: Date) => d.toISOString();
export const utcDay = (d: Date) => d.toISOString().slice(0, 10);

export const newId = () => {
  const b = crypto.getRandomValues(new Uint8Array(16));
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
};

export async function sha256Hex(s: string | Uint8Array): Promise<string> {
  const data = typeof s === "string" ? new TextEncoder().encode(s) : s;
  const h = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
  return [...h].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export const JSON_HEADERS = { "content-type": "application/json; charset=utf-8" };
export function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { ...JSON_HEADERS, ...headers } });
}

/** What's missing for each part of the Community Hub, so errors say exactly what to configure. */
export function missing(env: Env): { data: string[]; uploads: string[]; signIn: string[] } {
  const data = env.DB ? [] : ["D1 database binding DB"];
  const uploads = [...data];
  if (!env.IMAGES) uploads.push("Cloudflare Images binding IMAGES");
  const signIn = [...data];
  if (!env.GITHUB_CLIENT_ID) signIn.push("GITHUB_CLIENT_ID variable");
  if (!env.GITHUB_CLIENT_SECRET) signIn.push("GITHUB_CLIENT_SECRET secret");
  return { data, uploads, signIn };
}
