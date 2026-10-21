// V2 credentials are kept in a same-origin HttpOnly cookie by the Node proxy.
export class V2Error extends Error {
  constructor(status, data = {}) {
    super(data.detail || data.title || `请求失败（${status}）`);
    this.status = status; this.code = data.code || `HTTP_${status}`; this.errors = data.errors || {};
    this.subjects = Array.isArray(data.subjects) ? data.subjects.filter(item => item && typeof item.type === "string" && typeof item.id === "string")
      .map(({ type, id, label }) => ({ type, id, ...(typeof label === "string" ? { label } : {}) })) : [];
    this.retryable = status === 0 || [408, 429, 500, 502, 503, 504].includes(status) || this.code === "CONCURRENCY_CONFLICT";
  }
}
// Plain text only: callers must assign textContent, never insert this string as HTML.
export function describeV2Error(error) {
  const fields = Object.entries(error.errors ?? {}).map(([key, values]) => `；${key}：${[].concat(values).join("，")}`).join("");
  const labels = { bin: "库位", product: "商品", serializedItem: "实物", transaction: "流水", countTask: "盘点任务", countResult: "盘点结果", user: "账号", unit: "单位", category: "分类", settings: "设置", alert: "提醒" };
  const subjects = (error.subjects ?? []).slice(0, 10).map(item => `${labels[item.type] || item.type} ${item.label || item.id}`).join("、");
  return `${error.message}${error.code ? ` · ${error.code}` : ""}${fields}${subjects ? `；关联对象：${subjects}${error.subjects.length > 10 ? `（共 ${error.subjects.length} 项，仅显示前 10 项）` : ""}` : ""}`;
}
export function createV2Client({ fetchImpl = globalThis.fetch.bind(globalThis), sessionEnded = () => {} } = {}) {
  let epoch = 0;
  const active = new Set();
  async function request(method, path, body, key) {
    if (!path.startsWith("/api/v2/") || path.includes("..")) throw new Error("Unsupported API path");
    const started = epoch;
    const controller = new AbortController(); active.add(controller);
    const timeout = setTimeout(() => controller.abort(), 22000);
    try {
      const response = await fetchImpl(path, { method, credentials: "same-origin", cache: "no-store", signal: controller.signal,
        headers: { Accept: "application/json", ...(body === undefined ? {} : { "Content-Type": "application/json" }),
          "X-Correlation-ID": `v2-${crypto.randomUUID()}`, ...(key ? { "Idempotency-Key": key } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body) });
      const text = await response.text();
      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch { throw new V2Error(502, { title: "后端返回了无效 JSON。" }); }
      if (started !== epoch) throw new V2Error(409, { code: "SESSION_CHANGED", title: "会话已改变，旧请求结果已丢弃。" });
      if (!response.ok) {
        if (response.status === 401 && !path.endsWith("/login")) { invalidate(); sessionEnded(); }
        const error = new V2Error(response.status, data ?? {});
        error.retryAfter = response.headers.get("Retry-After");
        if (error.retryAfter) {
          const seconds = Number(error.retryAfter);
          error.retryAfterSeconds = Number.isFinite(seconds) ? Math.max(0, seconds) : Math.max(0, (Date.parse(error.retryAfter) - Date.now()) / 1000) || 0;
        }
        throw error;
      }
      return data;
    } catch (error) {
      if (error instanceof V2Error) throw error;
      if (started !== epoch) throw new V2Error(409, { code: "SESSION_CHANGED", title: "会话已改变，旧请求已停止。" });
      throw new V2Error(0, { code: "NETWORK_ERROR", title: "连接中断或请求超时。库存操作请使用原请求重试。" });
    } finally { clearTimeout(timeout); active.delete(controller); }
  }
  function invalidate() { epoch++; for (const controller of active) controller.abort(); }
  return {
    invalidate,
    get: (path, query = {}) => {
      const params = new URLSearchParams(Object.entries(query).filter(([, v]) => v !== "" && v !== undefined && v !== null));
      return request("GET", `/api/v2/${path}${params.size ? `?${params}` : ""}`);
    },
    send: (method, path, body = {}, key) => request(method, `/api/v2/${path}`, body, key)
  };
}

// A frozen request survives network failures without regenerating its idempotency key.
export function actionRequest(method, path, body) {
  const freeze = value => { if (value && typeof value === "object") { Object.values(value).forEach(freeze); Object.freeze(value); } return value; };
  return Object.freeze({ method, path, body: freeze(JSON.parse(JSON.stringify(body))), key: crypto.randomUUID() });
}
export function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}
export function imageDataUrl(base64, contentType) {
  if (!base64) return null;
  if (!["image/png", "image/jpeg"].includes(contentType) || base64.length > 2796204 || !/^[A-Za-z0-9+/]+={0,2}$/.test(base64)) throw new Error("商品图片格式无效。");
  return `data:${contentType};base64,${base64}`;
}

export function decimal(value, precision = 6, { positive = false, signed = false } = {}) {
  if (!Number.isInteger(precision) || precision < 0 || precision > 6) throw new Error("精度必须为 0～6。");
  const text = String(value).trim();
  const pattern = signed ? /^-?\d+(?:\.\d+)?$/ : /^\d+(?:\.\d+)?$/;
  if (!pattern.test(text)) throw new Error("数量必须为十进制数字，不支持科学计数法。");
  const [whole, fraction = ""] = text.replace(/^-/, "").split(".");
  if (fraction.length > 6 || fraction.replace(/0+$/, "").length > precision) throw new Error(`最多允许 ${precision} 位有效小数。`);
  const scaled = BigInt(whole) * 1000000n + BigInt(fraction.padEnd(6, "0"));
  if (scaled > 999999999999999999n || ((positive || signed) && scaled === 0n)) throw new Error("数量超出范围或不能为零。");
  return text;
}
