// Untrusted upload → safe image. Steps: size limit → file signature (PNG, JPEG, WebP only) → header parse
// (dimensions, transparency, animation) → Cloudflare Images decodes and re-encodes from pixels (full size + thumbnail)
// → the output is checked again and stripped of any metadata chunks/segments. Nothing of the original file is stored.

import { ApiError, type Env, type Limits } from "./env";

export type Mime = "image/png" | "image/jpeg" | "image/webp";

export interface Header { mime: Mime; width: number; height: number; alpha: boolean; animated: boolean }
export interface Processed { full: Uint8Array; fullMime: Mime; thumb: Uint8Array; thumbMime: Mime; width: number; height: number; alpha: boolean }

const u32 = (d: Uint8Array, i: number) => ((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]) >>> 0;
const u16 = (d: Uint8Array, i: number) => (d[i] << 8) | d[i + 1];
const le16 = (d: Uint8Array, i: number) => d[i] | (d[i + 1] << 8);
const le24 = (d: Uint8Array, i: number) => d[i] | (d[i + 1] << 8) | (d[i + 2] << 16);
const ascii = (d: Uint8Array, i: number, n: number) => String.fromCharCode(...d.subarray(i, i + n));
const PNG_SIG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

export function sniff(d: Uint8Array): Mime | null {
  if (d.length >= 8 && PNG_SIG.every((b, i) => d[i] === b)) return "image/png";
  if (d.length >= 3 && d[0] === 0xff && d[1] === 0xd8 && d[2] === 0xff) return "image/jpeg";
  if (d.length >= 12 && ascii(d, 0, 4) === "RIFF" && ascii(d, 8, 4) === "WEBP") return "image/webp";
  return null;
}

/** Reads dimensions, transparency and animation from the header only (cheap: no decoding). Null if malformed. */
export function readHeader(d: Uint8Array): Header | null {
  const mime = sniff(d);
  if (mime === "image/png") {
    if (d.length < 33 || ascii(d, 12, 4) !== "IHDR") return null;
    const h: Header = { mime, width: u32(d, 16), height: u32(d, 20), alpha: d[25] === 4 || d[25] === 6, animated: false };
    for (let pos = 8; pos + 8 <= d.length;) {
      const len = u32(d, pos), type = ascii(d, pos + 4, 4);
      if (type === "acTL") h.animated = true;
      if (type === "tRNS") h.alpha = true;
      if (type === "IDAT" || type === "IEND") break;
      if (pos + 12 + len > d.length) return null;
      pos += 12 + len;
    }
    return h;
  }
  if (mime === "image/jpeg") {
    for (let pos = 2; pos + 4 <= d.length;) {
      if (d[pos] !== 0xff) return null;
      const m = d[pos + 1];
      if (m === 0xff) { pos++; continue; }
      if (m === 0xd8 || (m >= 0xd0 && m <= 0xd7) || m === 0x01) { pos += 2; continue; }
      const len = u16(d, pos + 2);
      if (len < 2 || pos + 2 + len > d.length) return null;
      if ((m >= 0xc0 && m <= 0xcf) && m !== 0xc4 && m !== 0xc8 && m !== 0xcc)
        return { mime, width: u16(d, pos + 7), height: u16(d, pos + 5), alpha: false, animated: false };
      if (m === 0xda) return null; // image data before a frame header
      pos += 2 + len;
    }
    return null;
  }
  if (mime === "image/webp") {
    if (d.length < 30) return null;
    const type = ascii(d, 12, 4);
    if (type === "VP8X") return { mime, alpha: (d[20] & 0x10) !== 0, animated: (d[20] & 0x02) !== 0, width: le24(d, 24) + 1, height: le24(d, 27) + 1 };
    if (type === "VP8 ") return { mime, alpha: false, animated: false, width: le16(d, 26) & 0x3fff, height: le16(d, 28) & 0x3fff };
    if (type === "VP8L") {
      if (d[20] !== 0x2f) return null;
      const b = d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24);
      return { mime, width: (b & 0x3fff) + 1, height: ((b >>> 14) & 0x3fff) + 1, alpha: ((b >>> 28) & 1) === 1, animated: false };
    }
    return null;
  }
  return null;
}

/** Copies only the image-essential parts: no text, EXIF, XMP, ICC, timestamps or trailing data. */
export function stripMetadata(d: Uint8Array): Uint8Array {
  const mime = sniff(d);
  const parts: Uint8Array[] = [];
  if (mime === "image/png") {
    parts.push(d.subarray(0, 8));
    const keep = new Set(["IHDR", "PLTE", "tRNS", "IDAT", "IEND", "gAMA", "sRGB", "cHRM"]);
    for (let pos = 8; pos + 12 <= d.length;) {
      const len = u32(d, pos), type = ascii(d, pos + 4, 4), end = pos + 12 + len;
      if (end > d.length) throw new ApiError(422, "unreadable", "The image couldn't be read. It may be damaged.");
      if (keep.has(type)) parts.push(d.subarray(pos, end));
      pos = end;
      if (type === "IEND") break;
    }
  } else if (mime === "image/jpeg") {
    parts.push(d.subarray(0, 2));
    let pos = 2;
    for (; pos + 4 <= d.length;) {
      const m = d[pos + 1];
      if (d[pos] !== 0xff) throw new ApiError(422, "unreadable", "The image couldn't be read. It may be damaged.");
      const len = u16(d, pos + 2), end = pos + 2 + len;
      if (m === 0xda) break; // start of scan: the rest is image data
      const drop = (m >= 0xe1 && m <= 0xef && m !== 0xee) || m === 0xfe; // APP1–APP15 (except Adobe APP14) and comments
      if (!drop) parts.push(d.subarray(pos, end));
      pos = end;
    }
    let last = d.length;
    for (let i = d.length - 2; i > pos; i--) if (d[i] === 0xff && d[i + 1] === 0xd9) { last = i + 2; break; }
    parts.push(d.subarray(pos, last));
  } else return d;
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

const stream = (d: Uint8Array) => new Blob([d]).stream() as ReadableStream<Uint8Array>;
const bytesOf = async (r: ReadableStream<Uint8Array>) => new Uint8Array(await new Response(r).arrayBuffer());

export async function processUpload(env: Env, lim: Limits, data: Uint8Array, declared: string | null): Promise<Processed> {
  if (data.length === 0) throw new ApiError(400, "empty", "The file is empty.");
  if (data.length > lim.maxUploadBytes) throw new ApiError(413, "too_large", `Images can be at most ${Math.floor(lim.maxUploadBytes / 1048576)} MB.`);
  const mime = sniff(data);
  if (!mime) throw new ApiError(415, "unsupported_type", "Only PNG, JPEG and WebP images are accepted.");
  const d = (declared ?? "").split(";")[0].trim().toLowerCase();
  if (d && d !== "application/octet-stream" && d !== mime && !(d === "image/jpg" && mime === "image/jpeg"))
    throw new ApiError(415, "type_mismatch", "The file's contents don't match its type.");
  const h = readHeader(data);
  if (!h) throw new ApiError(422, "unreadable", "The image couldn't be read. It may be damaged.");
  if (h.width < lim.minDimension || h.height < lim.minDimension) throw new ApiError(422, "too_small", `Images must be at least ${lim.minDimension}×${lim.minDimension} pixels.`);
  if (h.width > lim.maxDimension || h.height > lim.maxDimension) throw new ApiError(422, "too_big", `Images can be at most ${lim.maxDimension}×${lim.maxDimension} pixels.`);
  if (h.animated) throw new ApiError(422, "animated", "Animated images aren't supported.");
  if (!env.IMAGES) throw new ApiError(503, "not_configured", "Image uploads aren't available: this server has no Cloudflare Images binding (IMAGES) to safely re-encode them.");

  let info;
  try { info = await env.IMAGES.info(stream(data)); }
  catch { throw new ApiError(422, "unreadable", "The image couldn't be read. It may be damaged."); }
  if (!("width" in info) || info.width !== h.width || info.height !== h.height)
    throw new ApiError(422, "unreadable", "The image couldn't be read. It may be damaged.");

  const outMime: Mime = h.alpha ? "image/png" : "image/jpeg";
  let full: Uint8Array, thumb: Uint8Array;
  try {
    full = await bytesOf((await env.IMAGES.input(stream(data)).output({ format: outMime, quality: 88, anim: false })).image());
    thumb = await bytesOf((await env.IMAGES.input(stream(data)).transform({ width: lim.thumbSize, height: lim.thumbSize, fit: "scale-down" })
      .output({ format: outMime, quality: 80, anim: false })).image());
  } catch (e) {
    // 9422: the Cloudflare Images free plan's 5,000 transformations for this month are used up (never charged).
    if (String((e as Error)?.message ?? e).includes("9422"))
      throw new ApiError(503, "processing_limit", "Image uploads are paused until next month: the Community Hub has used this month's free image processing. Posts without images still work.");
    throw new ApiError(422, "unreadable", "The image couldn't be processed. It may be damaged.");
  }
  if (sniff(full) !== outMime || sniff(thumb) !== outMime) throw new ApiError(500, "processing_failed", "The image couldn't be processed.");
  full = stripMetadata(full);
  thumb = stripMetadata(thumb);
  return { full, fullMime: outMime, thumb, thumbMime: outMime, width: h.width, height: h.height, alpha: h.alpha };
}
