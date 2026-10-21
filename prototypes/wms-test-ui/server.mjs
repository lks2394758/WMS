import { createReadStream } from "node:fs";
import { createServer, request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { proxyV2 } from "./v2-proxy.mjs";

// Static files are a fixed allow-list: nothing else in this folder (server code, tests, package.json) is served.
const root = fileURLToPath(new URL(".", import.meta.url));
const staticFiles = {
  "/v2": ["v2.html", "text/html; charset=utf-8"],
  "/v2/": ["v2.html", "text/html; charset=utf-8"],
  "/v2.html": ["v2.html", "text/html; charset=utf-8"],
  "/v2-app.js": ["v2-app.js", "text/javascript; charset=utf-8"],
  "/v2-client.js": ["v2-client.js", "text/javascript; charset=utf-8"],
  "/v2-model.js": ["v2-model.js", "text/javascript; charset=utf-8"],
  "/v2-labels.js": ["v2-labels.js", "text/javascript; charset=utf-8"],
  "/v2.css": ["v2.css", "text/css; charset=utf-8"],
  "/": ["index.html", "text/html; charset=utf-8"],
  "/index.html": ["index.html", "text/html; charset=utf-8"],
  "/app.js": ["app.js", "text/javascript; charset=utf-8"],
  "/api-client.js": ["api-client.js", "text/javascript; charset=utf-8"],
  "/styles.css": ["styles.css", "text/css; charset=utf-8"]
};

// Only the v1 endpoints the UI uses (contracts/openapi/wms.v1.json) and readiness. Anything else gets 404 here and
// never reaches the backend, so the proxy cannot be used to reach other paths or hosts.
const guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
const proxyRoutes = [
  ["POST", /^\/api\/v1\/auth\/(login|refresh)$/],
  ["GET", /^\/api\/v1\/auth\/me$/],
  ["GET", new RegExp(`^/api/v1/(products|locations)(/${guid})?$`)],
  ["GET", /^\/api\/v1\/barcodes\/resolve$/],
  ["GET", /^\/api\/v1\/inventory\/balances$/],
  ["POST", /^\/api\/v1\/inventory\/(receipts|shipments|movements|counts|adjustments)$/],
  ["GET", /^\/health\/ready$/]
];

const forwardedRequestHeaders = ["accept", "authorization", "content-type", "idempotency-key", "x-correlation-id"];
const forwardedResponseHeaders = ["content-type", "retry-after", "idempotent-replayed", "x-correlation-id", "www-authenticate"];
const maxBodyBytes = 64 * 1024;

const securityHeaders = {
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
  "Content-Security-Policy": "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"
};

/** Reads the configuration from the environment. The backend URL must be a plain http(s) origin. */
export function loadConfig(env = process.env) {
  const backend = new URL(env.WMS_API_BASE_URL ?? "http://127.0.0.1:5056");
  if (!["http:", "https:"].includes(backend.protocol) || backend.username || backend.password || backend.pathname !== "/" || backend.search || backend.hash) {
    throw new Error("WMS_API_BASE_URL must be an http(s) origin such as http://127.0.0.1:5056, without path, query or credentials.");
  }
  const v2Backend = new URL(env.WMS_V2_API_BASE_URL ?? env.WMS_API_BASE_URL ?? "http://127.0.0.1:5057");
  if (!["http:", "https:"].includes(v2Backend.protocol) || v2Backend.username || v2Backend.password || v2Backend.pathname !== "/" || v2Backend.search || v2Backend.hash) {
    throw new Error("WMS_V2_API_BASE_URL must be a plain http(s) origin without path, query or credentials.");
  }
  return {
    port: Number.parseInt(env.PORT ?? "4173", 10),
    host: env.HOST ?? "127.0.0.1",
    backend,
    v2Backend,
    timeoutMs: Number.parseInt(env.WMS_PROXY_TIMEOUT_MS ?? "15000", 10)
  };
}

function sendProblem(response, status, code, title, detail, extraHeaders = {}) {
  if (response.headersSent) {
    response.destroy();
    return;
  }
  response.writeHead(status, { ...securityHeaders, ...extraHeaders, "Content-Type": "application/problem+json; charset=utf-8" });
  response.end(JSON.stringify({ type: "about:blank", title, status, code, detail }));
}

function isProxyRoute(method, pathname) {
  return proxyRoutes.some(([allowed, pattern]) => allowed === method && pattern.test(pathname));
}

function proxy(request, response, url, config, log) {
  const target = new URL(config.backend);
  target.pathname = url.pathname;
  target.search = url.search;

  const headers = {};
  for (const name of forwardedRequestHeaders) {
    if (request.headers[name] !== undefined) headers[name] = request.headers[name];
  }
  if (request.method === "POST" && request.headers["content-length"] !== undefined) {
    const length = Number(request.headers["content-length"]);
    if (!Number.isFinite(length) || length > maxBodyBytes) {
      sendProblem(response, 413, "PAYLOAD_TOO_LARGE", "Request body too large", `The UI proxy accepts at most ${maxBodyBytes} bytes.`);
      request.resume();
      log(413);
      return;
    }
    headers["content-length"] = String(length);
  }

  const send = target.protocol === "https:" ? httpsRequest : httpRequest;
  let finished = false;
  const upstream = send(target, { method: request.method, headers, signal: AbortSignal.timeout(config.timeoutMs) }, upstreamResponse => {
    const outgoing = { ...securityHeaders };
    for (const name of forwardedResponseHeaders) {
      if (upstreamResponse.headers[name] !== undefined) outgoing[name] = upstreamResponse.headers[name];
    }
    response.writeHead(upstreamResponse.statusCode ?? 502, outgoing);
    upstreamResponse.pipe(response);
    upstreamResponse.on("end", () => { finished = true; log(upstreamResponse.statusCode); });
    upstreamResponse.on("error", () => response.destroy());
  });

  upstream.on("error", error => {
    if (finished) return;
    finished = true;
    if (error.name === "AbortError" || error.name === "TimeoutError") {
      sendProblem(response, 504, "API_TIMEOUT", "The WMS API did not answer in time",
        `No response within ${config.timeoutMs} ms. A write may or may not have been committed; retry it with the same Idempotency-Key.`);
      log(504);
    } else {
      sendProblem(response, 502, "API_UNAVAILABLE", "The WMS API is unavailable",
        `The UI server could not reach the configured API (${error.code ?? error.name}). Is it running?`);
      log(502);
    }
  });

  // The browser went away: stop the upstream request too (a write that already reached the API still commits).
  response.on("close", () => { if (!response.writableFinished) upstream.destroy(); });

  let received = 0;
  request.on("data", chunk => {
    received += chunk.length;
    if (received > maxBodyBytes) {
      upstream.destroy();
      finished = true;
      sendProblem(response, 413, "PAYLOAD_TOO_LARGE", "Request body too large", `The UI proxy accepts at most ${maxBodyBytes} bytes.`);
      log(413);
      request.resume();
    }
  });
  request.pipe(upstream);
}

function serveStatic(response, pathname) {
  const entry = staticFiles[pathname];
  if (!entry) {
    response.writeHead(404, { ...securityHeaders, "Content-Type": "text/plain; charset=utf-8" });
    response.end("Not found");
    return 404;
  }
  const [file, type] = entry;
  const stream = createReadStream(join(root, file));
  stream.on("open", () => {
    response.writeHead(200, { ...securityHeaders, "Content-Type": type });
    stream.pipe(response);
  });
  stream.on("error", () => sendProblem(response, 500, "STATIC_FILE_ERROR", "Static file unavailable", file));
  return 200;
}

/** Creates the UI server: static files plus the same-origin reverse proxy to the single configured backend. */
export function createAppServer(config, { logger = console } = {}) {
  return createServer((request, response) => {
    const started = performance.now();
    let url;
    try {
      url = new URL(request.url ?? "/", "http://ui.local");
    } catch {
      sendProblem(response, 400, "BAD_REQUEST", "Bad request", "The request URL is not valid.");
      return;
    }
    const isApi = url.pathname.startsWith("/api/") || url.pathname.startsWith("/health/");
    // Logs carry the method, the path (no query string: barcodes and cursors stay out) and the status. Never headers or bodies.
    const log = status => logger.log(`${request.method} ${url.pathname} -> ${status} ${Math.round(performance.now() - started)} ms`);

    if (isApi) {
      if (url.pathname.startsWith("/api/v2/")) {
        proxyV2(request, response, url, { ...config, backend: config.v2Backend ?? config.backend }, logger).catch(() => {
          if (!response.headersSent) sendProblem(response, 502, "API_UNAVAILABLE", "V2 proxy failure", "Unable to complete the API request.");
          else response.destroy();
        });
        return;
      }
      if (isProxyRoute(request.method, url.pathname)) {
        proxy(request, response, url, config, log);
      } else {
        sendProblem(response, 404, "NOT_PROXIED", "Not available through the UI proxy",
          "The UI server forwards only the WMS v1 endpoints the test UI uses.");
        request.resume();
        log(404);
      }
      return;
    }
    if (request.method !== "GET" && request.method !== "HEAD") {
      sendProblem(response, 405, "METHOD_NOT_ALLOWED", "Method not allowed", "Static files are read-only.", { Allow: "GET, HEAD" });
      request.resume();
      return;
    }
    serveStatic(response, url.pathname);
  });
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const config = loadConfig();
  createAppServer(config).listen(config.port, config.host, () => {
    console.log(`WMS test UI: http://${config.host === "127.0.0.1" ? "localhost" : config.host}:${config.port}`);
    console.log(`V1 proxy: ${config.backend.origin}; V2 proxy: ${config.v2Backend.origin} (timeout ${config.timeoutMs} ms)`);
  });
}
