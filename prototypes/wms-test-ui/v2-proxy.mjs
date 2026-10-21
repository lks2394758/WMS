// Local-only BFF: the browser gets an HttpOnly cookie, never the API credential.
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";

const id = "[1-9][0-9]{0,18}";
const routes = [
  ["POST", /^\/api\/v2\/auth\/(login|logout|switch-role)$/],
  ["GET", /^\/api\/v2\/auth\/me$/],
  ["GET", /^\/api\/v2\/(dashboard|units|categories|products|bins|counts|alerts|activity-logs|serialized-items|users|settings)$/],
  ["POST", /^\/api\/v2\/(units|categories|products|bins|counts|users)$/],
  ["POST", /^\/api\/v2\/bins\/racks$/],
  ["GET", new RegExp(`^/api/v2/(products|bins|counts|serialized-items|users|inventory/transactions)/${id}$`)],
  ["PUT", new RegExp(`^/api/v2/(products|categories|units|bins|users)/${id}$`)],
  ["DELETE", new RegExp(`^/api/v2/(products|categories|units|bins)/${id}$`)],
  ["PUT", /^\/api\/v2\/settings$/],
  ["POST", new RegExp(`^/api/v2/counts/${id}/(submit|confirm|recount|cancel|resolve)$`)],
  ["POST", new RegExp(`^/api/v2/serialized-items/${id}/correct$`)],
  ["POST", new RegExp(`^/api/v2/users/${id}/reset-password$`)],
  ["GET", /^\/api\/v2\/inventory\/transactions$/],
  ["POST", /^\/api\/v2\/inventory\/(receipts|shipments|movements|adjustments)$/]
];
const cookieName = "wms_v2_session";
const limit = 3 * 1024 * 1024;
const headers = { "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" };
const cookie = (value, seconds) => `${cookieName}=${encodeURIComponent(value)}; Path=/api/v2; HttpOnly; SameSite=Strict; Max-Age=${seconds}`;
function problem(res, status, code, detail) {
  res.writeHead(status, { ...headers, "Content-Type": "application/problem+json" });
  res.end(JSON.stringify({ status, code, title: detail }));
}
export function allowsV2(method, path) {
  return routes.some(([verb, pattern]) => verb === method && pattern.test(path));
}
export async function proxyV2(req, res, url, config, logger = console) {
  const started = performance.now();
  const log = status => logger.log(`${req.method} /api/v2/[route] -> ${status} ${Math.round(performance.now() - started)} ms`);
  let host;
  try { host = new URL(`http://${req.headers.host}`); } catch { host = null; }
  if (!host || !["localhost", "127.0.0.1", "[::1]"].includes(host.hostname) || Number(host.port || 80) !== req.socket.localPort) {
    req.resume(); problem(res, 403, "HOST_REJECTED", "新版代理仅接受本机地址访问。"); return;
  }
  if (!allowsV2(req.method, url.pathname)) {
    req.resume(); problem(res, 404, "NOT_PROXIED", "新版代理未开放此接口。"); return;
  }
  if (!["GET", "HEAD"].includes(req.method)) {
    // SameSite cookies alone are not a replacement for validating the write origin.
    const origin = req.headers.origin;
    const expected = `http://${req.headers.host}`;
    if ((origin && origin !== expected) || req.headers["sec-fetch-site"] === "cross-site") {
      req.resume(); problem(res, 403, "ORIGIN_REJECTED", "不接受其他网站发起的写入。"); return;
    }
    if (req.headers["content-type"]?.split(";")[0].trim() !== "application/json") {
      req.resume(); problem(res, 415, "JSON_REQUIRED", "请求必须使用 JSON。"); return;
    }
  }
  let size = 0;
  const chunks = [];
  if (Number(req.headers["content-length"]) > limit) { req.resume(); problem(res, 413, "PAYLOAD_TOO_LARGE", "请求超过 3 MiB。"); return; }
  for await (const chunk of req) {
    size += chunk.length;
    if (size > limit) { problem(res, 413, "PAYLOAD_TOO_LARGE", "请求超过 3 MiB。"); return; }
    chunks.push(chunk);
  }
  const body = Buffer.concat(chunks);
  const outgoing = { Accept: "application/json" };
  for (const key of ["content-type", "idempotency-key", "x-correlation-id"]) {
    if (req.headers[key]) outgoing[key] = req.headers[key];
  }
  let credential;
  const raw = req.headers.cookie?.split(";").map(s => s.trim()).find(s => s.startsWith(`${cookieName}=`));
  try { credential = raw ? decodeURIComponent(raw.slice(cookieName.length + 1)) : null; } catch { credential = null; }
  if (credential && /^[A-Za-z0-9._~+\/-]+=*$/.test(credential)) outgoing.Authorization = `Bearer ${credential}`;
  if (body.length) outgoing["Content-Length"] = String(body.length);
  const target = new URL(url.pathname + url.search, config.backend);
  const send = target.protocol === "https:" ? httpsRequest : httpRequest;
  await new Promise(resolve => {
    let finished = false;
    const done = () => { if (!finished) { finished = true; resolve(); } };
    const upstream = send(target, { method: req.method, headers: outgoing, signal: AbortSignal.timeout(config.timeoutMs) }, incoming => {
      const responseHeaders = { ...headers };
      for (const key of ["content-type", "retry-after", "idempotent-replayed", "x-correlation-id", "www-authenticate"]) {
        if (incoming.headers[key]) responseHeaders[key] = incoming.headers[key];
      }
      const status = incoming.statusCode ?? 502;
      const authResponse = /^\/api\/v2\/auth\/(login|switch-role)$/.test(url.pathname) && status >= 200 && status < 300;
      if (status === 401 || (url.pathname === "/api/v2/auth/logout" && status < 300)) responseHeaders["Set-Cookie"] = cookie("", 0);
      if (authResponse) {
        let count = 0;
        const parts = [];
        incoming.on("data", chunk => { count += chunk.length; if (count > 65536) upstream.destroy(new Error("Oversized auth response")); else parts.push(chunk); });
        incoming.on("end", () => {
          try {
            const payload = JSON.parse(Buffer.concat(parts).toString("utf8"));
            const token = payload.token ?? payload.accessToken;
            if (token) {
              if (!/^[A-Za-z0-9._~+\/-]+=*$/.test(token) || !Number.isFinite(Date.parse(payload.expiresAt ?? payload.accessTokenExpiresAt))) throw new Error("Invalid session response");
              const seconds = Math.floor((Date.parse(payload.expiresAt ?? payload.accessTokenExpiresAt) - Date.now()) / 1000);
              responseHeaders["Set-Cookie"] = cookie(token, Math.max(0, Math.min(seconds, 168 * 3600)));
            } else if (url.pathname.endsWith("/login")) throw new Error("Missing session credential");
            delete payload.token; delete payload.accessToken; delete payload.refreshToken;
            res.writeHead(status, responseHeaders); res.end(JSON.stringify(payload)); log(status); done();
          } catch { problem(res, 502, "INVALID_SESSION_RESPONSE", "后端会话响应不符合新版接口约定。"); log(502); done(); }
        });
      } else {
        res.writeHead(status, responseHeaders);
        incoming.pipe(res);
        incoming.on("end", () => { log(status); done(); });
      }
      incoming.on("error", () => { res.destroy(); done(); });
    });
    upstream.on("error", error => {
      if (!res.headersSent) {
        const timed = ["AbortError", "TimeoutError"].includes(error.name);
        problem(res, timed ? 504 : 502, timed ? "API_TIMEOUT" : "API_UNAVAILABLE", "无法连接新版后端。请求可能已提交，请保留原请求编号重试。"); log(timed ? 504 : 502);
      } else res.destroy();
      done();
    });
    res.on("close", () => { if (!res.writableFinished) upstream.destroy(); done(); });
    upstream.end(body);
  });
}
