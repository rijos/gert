// services/http.js — fetch wrapper: base URL, in-memory Bearer header, JSON,
// error shaping. The ONLY token reader is services/auth.js (F2).
import { getToken } from "./auth.js";

const BASE = "/api";

const authHeaders = (extra = {}) => {
  const h = { ...extra };
  const t = getToken();
  if (t) h.Authorization = "Bearer " + t;
  return h;
};

export class ApiError extends Error {
  constructor(status, message, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

const handle = async (res) => {
  if (res.status === 204) return null;
  const ct = res.headers.get("content-type") || "";
  const data = ct.includes("application/json")
    ? await res.json().catch(() => null)
    : await res.text();
  if (!res.ok) {
    const msg = (data && data.message) || res.statusText || "request failed";
    throw new ApiError(res.status, msg, data);
  }
  return data;
};

export const get = (path) =>
  fetch(BASE + path, { headers: authHeaders() }).then(handle);

export const post = (path, body) =>
  fetch(BASE + path, {
    method: "POST",
    headers: authHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify(body ?? {}),
  }).then(handle);

export const patch = (path, body) =>
  fetch(BASE + path, {
    method: "PATCH",
    headers: authHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify(body ?? {}),
  }).then(handle);

export const put = (path, body) =>
  fetch(BASE + path, {
    method: "PUT",
    headers: authHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify(body ?? {}),
  }).then(handle);

export const del = (path) =>
  fetch(BASE + path, { method: "DELETE", headers: authHeaders() }).then(handle);

// multipart upload (FormData) — do NOT set Content-Type; the browser adds the boundary.
export const upload = (path, formData) =>
  fetch(BASE + path, {
    method: "POST",
    headers: authHeaders(),
    body: formData,
  }).then(handle);

// Open a GET SSE stream (the live conversation stream endpoint) and yield
// parsed { id, event, data } records — `id` is the seq cursor from the frame's
// `id:` field. Parses the EventSource wire format off a fetch body reader
// (EventSource itself can't send auth headers; fetch keeps the Bearer — F2).
export async function* sse(path) {
  const res = await fetch(BASE + path, {
    headers: authHeaders({ Accept: "text/event-stream" }),
  });
  if (!res.ok || !res.body) {
    throw new ApiError(res.status, "stream failed");
  }
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = "";
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });
    let sep;
    // events are separated by a blank line
    while ((sep = buf.indexOf("\n\n")) >= 0) {
      const raw = buf.slice(0, sep);
      buf = buf.slice(sep + 2);
      let event = "message";
      let id = null;
      const dataLines = [];
      for (const line of raw.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("id:")) id = Number(line.slice(3).trim());
        else if (line.startsWith("data:")) dataLines.push(line.slice(5).trim());
      }
      if (!dataLines.length) continue;
      let data;
      try {
        data = JSON.parse(dataLines.join("\n"));
      } catch {
        data = dataLines.join("\n");
      }
      yield { id, event, data };
    }
  }
}

// Open the chat WebSocket. The in-memory token rides as the second
// Sec-WebSocket-Protocol entry (F2 — a browser WebSocket cannot send an
// Authorization header, and the token never goes in the URL); the server's
// shim middleware authenticates it through the normal JwtBearer pipeline.
export const ws = (path) => {
  const scheme = location.protocol === "https:" ? "wss://" : "ws://";
  const url = scheme + location.host + BASE + path;
  const t = getToken();
  return t ? new WebSocket(url, ["bearer", t]) : new WebSocket(url);
};
