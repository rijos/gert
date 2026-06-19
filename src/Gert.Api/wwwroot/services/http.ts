// services/http.js - fetch wrapper: base URL, in-memory Bearer header, JSON,
// error shaping, and the app's one transient-failure policy (retry + degraded
// health) so callers never hand-roll retries. The ONLY token reader is auth.js (F2).
import { getToken } from "./auth.js";
import * as health from "../state/health.js";

const BASE = "/api";

const authHeaders = (extra: Record<string, string> = {}): Record<string, string> => {
  const h: Record<string, string> = { ...extra };
  const t = getToken();
  if (t) h.Authorization = "Bearer " + t;
  return h;
};

export class ApiError extends Error {
  status: number;
  body: unknown;
  constructor(status: number, message: string, body?: unknown) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

const handle = async (res: Response): Promise<unknown> => {
  if (res.status === 204) return null;
  const ct = res.headers.get("content-type") || "";
  const data: unknown = ct.includes("application/json")
    ? await res.json().catch(() => null)
    : await res.text();
  if (!res.ok) {
    // The error body, when JSON, may carry a `message` field; narrow before reading.
    const msg =
      (data && typeof data === "object" && typeof (data as { message?: unknown }).message === "string"
        ? (data as { message: string }).message
        : null) ||
      res.statusText ||
      "request failed";
    throw new ApiError(res.status, msg, data);
  }
  return data;
};

// Transient failure = no working server reached: a fetch rejection (no response)
// or a 5xx/408/429. Retried on idempotent GETs only (a blind write-retry could
// double-submit), and once retries are spent it flips `degraded`. A 4xx is the
// server answering (a real app error): never retried, never degraded. Any
// success clears degraded.
const RETRY_STATUS = new Set([408, 429, 500, 502, 503, 504]);
const MAX_ATTEMPTS = 3; // initial try + 2 retries
const BACKOFF_MS = [300, 900]; // before retry #1, #2

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

// server `Retry-After` (seconds) wins when present (429/503); else fixed backoff.
const backoffMs = (attempt: number, retryAfter: string | null): number => {
  const ra = retryAfter ? Number(retryAfter) * 1000 : NaN;
  return Number.isFinite(ra) ? Math.min(ra, 5000) : (BACKOFF_MS[attempt - 1] ?? 900);
};

// The one fetch chokepoint: retry policy + degraded health, then handle().
const send = async (path: string, init: RequestInit, retry: boolean): Promise<unknown> => {
  for (let attempt = 1; ; attempt++) {
    let res: Response;
    try {
      res = await fetch(BASE + path, init);
    } catch {
      // no response - a network/connection drop
      if (retry && attempt < MAX_ATTEMPTS) {
        await sleep(backoffMs(attempt, null));
        continue;
      }
      health.degraded.val = true;
      throw new ApiError(0, "network error");
    }
    if (retry && attempt < MAX_ATTEMPTS && (RETRY_STATUS.has(res.status) || res.status >= 500)) {
      await sleep(backoffMs(attempt, res.headers.get("retry-after")));
      continue;
    }
    // the server responded: a 5xx that outlived its retries is the poor state.
    health.degraded.val = res.status >= 500;
    return handle(res);
  }
};

// THE wire boundary cast. `handle` parses the body as `unknown` (it is, at runtime); the caller
// names the wire DTO it expects via `T` (see services/wire.ts). This single `as Promise<T>` per
// verb is the TS analogue of `JsonSerializer.Deserialize<T>` - the one place a runtime value is
// asserted to match a declared shape. `T` defaults to `unknown`, so a call that ignores the body
// (most DELETEs / fire-and-forget POSTs) stays honest without an annotation.
const jsonInit = (method: string, body?: unknown): RequestInit => ({
  method,
  headers: authHeaders({ "Content-Type": "application/json" }),
  body: JSON.stringify(body ?? {}),
});

export const get = <T = unknown>(path: string): Promise<T> =>
  send(path, { headers: authHeaders() }, true) as Promise<T>;

export const post = <T = unknown>(path: string, body?: unknown): Promise<T> =>
  send(path, jsonInit("POST", body), false) as Promise<T>;

export const patch = <T = unknown>(path: string, body?: unknown): Promise<T> =>
  send(path, jsonInit("PATCH", body), false) as Promise<T>;

export const put = <T = unknown>(path: string, body?: unknown): Promise<T> =>
  send(path, jsonInit("PUT", body), false) as Promise<T>;

export const del = <T = unknown>(path: string): Promise<T> =>
  send(path, { method: "DELETE", headers: authHeaders() }, false) as Promise<T>;

// multipart upload (FormData) - do NOT set Content-Type; the browser adds the boundary.
export const upload = <T = unknown>(path: string, formData: FormData): Promise<T> =>
  send(path, { method: "POST", headers: authHeaders(), body: formData }, false) as Promise<T>;

// One parsed SSE frame: `id` is the seq cursor from the frame's `id:` field
// (null when absent), `data` the JSON-parsed (or raw-string) `data:` payload.
export interface SseFrame {
  id: number | null;
  event: string;
  data: unknown;
}

// Open a GET SSE stream (the live conversation stream endpoint) and yield
// parsed { id, event, data } records - `id` is the seq cursor from the frame's
// `id:` field. Parses the EventSource wire format off a fetch body reader
// (EventSource itself can't send auth headers; fetch keeps the Bearer - F2).
export async function* sse(
  path: string,
  { signal }: { signal?: AbortSignal } = {},
): AsyncGenerator<SseFrame, void, void> {
  // The init always carries the `signal` key (value possibly undefined, as in
  // the JS original); the cast satisfies exactOptionalPropertyTypes at the
  // fetch boundary without changing the value passed to fetch.
  const res = await fetch(BASE + path, {
    headers: authHeaders({ Accept: "text/event-stream" }),
    signal,
  } as RequestInit);
  if (!res.ok || !res.body) {
    throw new ApiError(res.status, "stream failed");
  }
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = "";
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      let sep: number;
      // events are separated by a blank line
      while ((sep = buf.indexOf("\n\n")) >= 0) {
        const raw = buf.slice(0, sep);
        buf = buf.slice(sep + 2);
        let event = "message";
        let id: number | null = null;
        const dataLines: string[] = [];
        for (const line of raw.split("\n")) {
          if (line.startsWith("event:")) event = line.slice(6).trim();
          else if (line.startsWith("id:")) id = Number(line.slice(3).trim());
          else if (line.startsWith("data:")) dataLines.push(line.slice(5).trim());
        }
        if (!dataLines.length) continue;
        let data: unknown;
        try {
          data = JSON.parse(dataLines.join("\n"));
        } catch {
          data = dataLines.join("\n");
        }
        yield { id, event, data };
      }
    }
  } finally {
    // Generator finalization - the consumer returning early on a terminal
    // event, a throw, or natural end - tears the connection down with it
    // (section 12: revoke what you mint). No-op after a clean done/abort.
    reader.cancel().catch(() => {});
  }
}
