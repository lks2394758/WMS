// DOM-free client for the WMS v1 API, used by app.js and by api-client.test.mjs.
// Tokens live only in this object's memory: a page reload or sign-out forgets them.

export const requestTimeoutMs = 20000; // longer than the UI proxy's 15 s, so the proxy's clear 504 normally wins

/** One API failure, normalized from problem+json, a proxy error or a network failure. */
export class ApiError extends Error {
  constructor({ status, code, title, detail, errors, retryAfterSeconds, correlationId, uncertain }) {
    super(detail || title || code);
    this.status = status;
    this.code = code;
    this.title = title;
    this.errors = errors ?? {};
    this.retryAfterSeconds = retryAfterSeconds;
    this.correlationId = correlationId;
    // True when a write may or may not have committed (network failure, timeout, proxy 502/504, 500): retry it with
    // the same Idempotency-Key and body; the server replays or executes it exactly once.
    this.uncertain = uncertain;
  }
}

// Codes after which the same action (same key, same body) may safely be sent again.
const retryableCodes = new Set(["CONCURRENCY_CONFLICT", "RATE_LIMITED", "API_UNAVAILABLE", "API_TIMEOUT", "INTERNAL_ERROR", "NETWORK_ERROR"]);

export function isRetryable(error) {
  return error instanceof ApiError && (error.uncertain || retryableCodes.has(error.code));
}

function parseRetryAfter(value) {
  if (!value) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds)) return Math.max(0, Math.ceil(seconds));
  const date = Date.parse(value);
  return Number.isNaN(date) ? undefined : Math.max(0, Math.ceil((date - Date.now()) / 1000));
}

export function createApiClient({ fetchImpl = globalThis.fetch.bind(globalThis), onSessionEnded = () => {}, timeoutMs = requestTimeoutMs } = {}) {
  let session = null; // { accessToken, refreshToken, accessTokenExpiresAt, refreshTokenExpiresAt, user }
  let epoch = 0; // incremented on sign-in and sign-out; results of requests from an older epoch are discarded
  let refreshing = null; // single-flight: a refresh token may be used only once, a second use revokes the session

  async function send(method, path, { body, idempotencyKey, auth = true } = {}) {
    const headers = { Accept: "application/json", "X-Correlation-ID": `ui-${crypto.randomUUID().replaceAll("-", "")}` };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
    if (auth && session) headers.Authorization = `Bearer ${session.accessToken}`;

    let response;
    try {
      response = await fetchImpl(path, {
        method, headers, body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(timeoutMs), cache: "no-store", credentials: "omit"
      });
    } catch (error) {
      const timedOut = error?.name === "TimeoutError" || error?.name === "AbortError";
      throw new ApiError({
        status: 0, code: timedOut ? "API_TIMEOUT" : "NETWORK_ERROR", uncertain: true, correlationId: headers["X-Correlation-ID"],
        title: timedOut ? "请求超时" : "无法连接 UI 服务器",
        detail: timedOut ? `${Math.round(timeoutMs / 1000)} 秒内没有响应。` : "浏览器无法连接本地 UI 服务器（localhost:4173）。"
      });
    }

    const correlationId = response.headers.get("X-Correlation-ID") ?? headers["X-Correlation-ID"];
    const text = await response.text().catch(() => "");
    let payload = null;
    if (text) {
      try { payload = JSON.parse(text); } catch { payload = null; }
    }
    if (response.ok) {
      return { data: payload, replayed: response.headers.get("Idempotent-Replayed") === "true", correlationId };
    }
    throw new ApiError({
      status: response.status,
      code: payload?.code ?? (response.status === 401 ? "UNAUTHENTICATED" : `HTTP_${response.status}`),
      title: payload?.title ?? `HTTP ${response.status}`,
      detail: payload?.detail,
      errors: payload?.errors,
      retryAfterSeconds: parseRetryAfter(response.headers.get("Retry-After")),
      correlationId,
      uncertain: [500, 502, 504].includes(response.status)
    });
  }

  function adopt(login) {
    session = {
      accessToken: login.accessToken,
      refreshToken: login.refreshToken,
      accessTokenExpiresAt: login.accessTokenExpiresAt,
      refreshTokenExpiresAt: login.refreshTokenExpiresAt,
      user: login.user
    };
  }

  function end(reason) {
    session = null;
    refreshing = null;
    epoch += 1;
    onSessionEnded(reason);
  }

  async function refresh() {
    if (!refreshing) {
      const current = session;
      const startedEpoch = epoch;
      refreshing = send("POST", "/api/v1/auth/refresh", { body: { refreshToken: current.refreshToken }, auth: false })
        .then(({ data }) => {
          if (epoch === startedEpoch) adopt(data);
          return true;
        })
        .catch(error => {
          // Only a definite rejection ends the session; a network failure keeps it so the user can retry.
          if (epoch === startedEpoch && error.status === 401) end("expired");
          throw error;
        })
        .finally(() => { if (epoch === startedEpoch) refreshing = null; });
    }
    return refreshing;
  }

  /** Authenticated request. On 401 it refreshes once (shared by concurrent callers) and repeats the identical request. */
  async function call(method, path, options = {}) {
    if (!session) throw new ApiError({ status: 401, code: "UNAUTHENTICATED", title: "未登录", detail: "请先登录。" });
    const startedEpoch = epoch;
    const guard = result => {
      if (epoch !== startedEpoch) throw new ApiError({ status: 0, code: "SESSION_CHANGED", title: "会话已变更", detail: "登录状态已改变，结果已丢弃。" });
      return result;
    };
    try {
      return guard(await send(method, path, options));
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401 || epoch !== startedEpoch || !session) throw error;
    }
    await refresh();
    guard();
    try {
      return guard(await send(method, path, options));
    } catch (error) {
      if (error instanceof ApiError && error.status === 401 && epoch === startedEpoch) end("expired");
      throw error;
    }
  }

  return {
    get user() { return session?.user ?? null; },
    get signedIn() { return session !== null; },
    get epoch() { return epoch; },
    can(permission) { return Boolean(session?.user?.permissions?.includes(permission)); },

    async login(username, password) {
      const { data } = await send("POST", "/api/v1/auth/login", { body: { username, password }, auth: false });
      epoch += 1;
      refreshing = null;
      adopt(data);
      return data.user;
    },
    logout() { end("signedOut"); },
    refresh,
    me: () => call("GET", "/api/v1/auth/me"),
    get: (path, query = {}) => {
      const search = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== undefined && value !== null && value !== ""));
      return call("GET", search.size ? `${path}?${search}` : path);
    },
    /** Sends one inventory write. The caller owns the key and body, and passes the same ones again to retry. */
    write: (path, body, idempotencyKey) => call("POST", path, { body, idempotencyKey }),
    health: async () => {
      try {
        const response = await fetchImpl("/health/ready", { cache: "no-store", signal: AbortSignal.timeout(timeoutMs) });
        return response.status === 200 ? "ready" : response.status === 502 || response.status === 504 ? "unreachable" : "unhealthy";
      } catch {
        return "unreachable";
      }
    }
  };
}

/** Reads every page of a cursor list (bounded, so a faulty cursor cannot loop forever). */
export async function readAll(client, path, query = {}, maxPages = 50) {
  const items = [];
  let cursor;
  for (let page = 0; page < maxPages; page += 1) {
    const { data } = await client.get(path, { ...query, limit: 200, cursor });
    items.push(...(data?.items ?? []));
    cursor = data?.nextCursor;
    if (!cursor) return items;
  }
  throw new ApiError({ status: 0, code: "TOO_MANY_PAGES", title: "数据过多", detail: `超过 ${maxPages} 页，已停止读取。` });
}
