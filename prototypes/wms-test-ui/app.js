import { ApiError, createApiClient, isRetryable, readAll } from "./api-client.js";

// Every number on this page comes from the API. Nothing is computed into stock locally: after each write the page
// reads the committed balances again.

const client = createApiClient({ onSessionEnded: reason => resetSession(reason) });

const roleLabels = { systemAdministrator: "系统管理员", warehouseSupervisor: "仓库主管", receivingClerk: "收货员", shippingClerk: "发货员" };
const typeLabels = { receipt: "入库", shipment: "出库", movement: "移库", count: "盘点", adjustment: "调整" };

const quantityLine = (v, name) => [{ productId: v.product, [name]: v[name] }];
const operations = {
  receive: {
    label: "入库", permission: "Inventory.Receive", path: "/api/v1/inventory/receipts", locationLabel: "入库货位",
    fields: ["product", "location", "quantity", "referenceNo"],
    build: v => ({ locationId: v.location, lines: quantityLine(v, "quantity"), referenceNo: v.referenceNo })
  },
  ship: {
    label: "出库", permission: "Inventory.Ship", path: "/api/v1/inventory/shipments", locationLabel: "出库货位",
    fields: ["product", "location", "quantity", "referenceNo"],
    build: v => ({ locationId: v.location, lines: quantityLine(v, "quantity"), referenceNo: v.referenceNo })
  },
  move: {
    label: "移库", permission: "Inventory.Receive", path: "/api/v1/inventory/movements",
    fields: ["product", "fromLocation", "toLocation", "quantity", "referenceNo"],
    build: v => ({ fromLocationId: v.fromLocation, toLocationId: v.toLocation, lines: quantityLine(v, "quantity"), referenceNo: v.referenceNo })
  },
  count: {
    label: "盘点", permission: "Inventory.Count", path: "/api/v1/inventory/counts", locationLabel: "盘点货位",
    fields: ["product", "location", "countedQuantity", "reason"],
    build: v => ({ locationId: v.location, reason: v.reason, lines: quantityLine(v, "countedQuantity") })
  },
  adjust: {
    label: "调整", permission: "Inventory.Adjust", path: "/api/v1/inventory/adjustments", locationLabel: "调整货位",
    fields: ["product", "location", "quantityDelta", "reason", "referenceNo"],
    build: v => ({ locationId: v.location, reason: v.reason, lines: quantityLine(v, "quantityDelta"), referenceNo: v.referenceNo })
  }
};
const fieldLabels = {
  product: "商品", fromLocation: "来源货位", toLocation: "目标货位",
  quantity: "数量（正整数）", countedQuantity: "实盘数量（≥ 0）", quantityDelta: "调整数量（+增 / −减，非 0）",
  referenceNo: "参考单号（可选，≤ 64 字）", reason: "原因（必填，≤ 500 字）"
};
const numericFields = new Set(["quantity", "countedQuantity", "quantityDelta"]);
// Server field paths (RequestRules / model binding) to form fields.
const errorFields = {
  locationid: "location", fromlocationid: "fromLocation", tolocationid: "toLocation", reason: "reason", referenceno: "referenceNo",
  "lines[0].productid": "product", "lines[0].quantity": "quantity", "lines[0].countedquantity": "countedQuantity", "lines[0].quantitydelta": "quantityDelta"
};

const state = {
  products: [], locations: [], balances: [],
  catalogStatus: "idle", balanceStatus: "idle", loadError: null, loadedAt: null,
  history: [], activeOperation: "receive",
  pending: null, // a write whose outcome is unknown or that may be retried: { key, operation, path, body, summary }
  busy: false, retryAt: 0, retryTimer: null
};

const $ = selector => document.querySelector(selector);
const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[c]);
const formatTime = iso => iso ? new Intl.DateTimeFormat("zh-CN", { dateStyle: "short", timeStyle: "medium" }).format(new Date(iso)) : "";
const productById = id => state.products.find(p => p.id === id);
const locationById = id => state.locations.find(l => l.id === id);
const productName = id => { const p = productById(id); return p ? `${p.sku} · ${p.name}` : id; };
const locationCode = id => locationById(id)?.code ?? id;

function showToast(message) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => toast.classList.remove("show"), 3200);
}

function describeError(error) {
  if (!(error instanceof ApiError)) return `意外错误：${error?.message ?? error}`;
  const trace = error.correlationId ? `（关联 ID ${error.correlationId}）` : "";
  switch (error.code) {
    case "API_UNAVAILABLE": return `WMS API 不可达：UI 服务器无法连接后端。请确认 API 已启动。${trace}`;
    case "API_TIMEOUT": return `WMS API 响应超时。写入操作可能已提交，也可能没有；请用“重试”复用同一 Idempotency-Key。${trace}`;
    case "NETWORK_ERROR": return `浏览器无法连接 UI 服务器。写入操作可能已提交，也可能没有。${trace}`;
    case "RATE_LIMITED": return `请求过于频繁（429），请在 ${error.retryAfterSeconds ?? "若干"} 秒后重试。${trace}`;
    case "FORBIDDEN": return `当前角色没有此操作的权限（403）。${trace}`;
    case "UNAUTHENTICATED": return `未登录或会话已失效（401）。${trace}`;
    case "INSUFFICIENT_STOCK": return `库存不足（409）：${error.message}${trace}`;
    case "CONCURRENCY_CONFLICT": return `服务器未能及时获得库存锁（409），未保存任何内容。可用同一 Idempotency-Key 重试。${trace}`;
    case "IDEMPOTENCY_CONFLICT": return `Idempotency-Key 已用于不同的请求（409）。${trace}`;
    case "VALIDATION_FAILED": return `请求未通过服务器校验（400），请检查标出的字段。${trace}`;
    default: return `${error.title ?? "请求失败"}（${error.status || error.code}）${error.message && error.message !== error.title ? `：${error.message}` : ""}${trace}`;
  }
}

/* ---------- connectivity ---------- */

async function checkHealth() {
  const status = await client.health();
  const box = $("#api-status");
  const recovered = status === "ready" && box.dataset.state !== undefined && box.dataset.state !== "ready";
  box.dataset.state = status;
  box.lastElementChild.textContent = {
    ready: "已连接真实 WMS API",
    unhealthy: "WMS API 未就绪（/health/ready 非 200）",
    unreachable: "WMS API 不可达"
  }[status];
  // The API is back: replace an error state with fresh server data instead of waiting for the user.
  if (recovered && client.signedIn && !state.busy) {
    if (state.catalogStatus === "error") loadAll();
    else if (state.balanceStatus === "error") reloadBalances();
  }
}

/* ---------- session ---------- */

function showSignedIn(user) {
  $("#login-view").classList.add("hidden");
  $("#app-view").classList.remove("hidden");
  $("#session-box").classList.remove("hidden");
  $("#session-user").textContent = `${user.displayName}（${user.username} · ${roleLabels[user.role] ?? user.role}）`;
  $("#session-user").title = `权限：${user.permissions.join(", ") || "无写入权限"}`;
}

function resetSession(reason) {
  clearTimeout(state.retryTimer);
  Object.assign(state, {
    products: [], locations: [], balances: [], catalogStatus: "idle", balanceStatus: "idle", loadError: null, loadedAt: null,
    history: [], activeOperation: "receive", pending: null, busy: false, retryAt: 0, retryTimer: null
  });
  $("#app-view").classList.add("hidden");
  $("#session-box").classList.add("hidden");
  $("#login-view").classList.remove("hidden");
  // Remove the previous user's rendered data too, not only the state behind it.
  for (const selector of ["#operation-form", "#pending-banner", "#inventory-body", "#product-list", "#history-list", "#operation-tabs", "#filter-product", "#filter-location"]) $(selector).innerHTML = "";
  $("#lookup-value").value = "";
  $("#lookup-result").textContent = "";
  $("#operation-message").textContent = "";
  $("#login-message").textContent = reason === "expired" ? "会话已过期或已被吊销，请重新登录。" : reason === "signedOut" ? "已退出登录，本标签页中的令牌和会话记录已清除。" : "";
  $("#login-password").value = "";
  $("#login-username").focus();
}

$("#login-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const button = $("#login-button");
  if (button.disabled) return;
  const username = form.username.value.trim();
  const password = form.password.value;
  form.querySelectorAll("[data-error-for]").forEach(el => { el.textContent = ""; });
  $("#login-message").textContent = "";
  button.disabled = true;
  button.textContent = "登录中…";
  try {
    await client.login(username, password);
    form.password.value = "";
    const { data: me } = await client.me(); // the signed-in user as the API reports it
    showSignedIn(me);
    renderAll();
    await loadAll();
  } catch (error) {
    form.password.value = "";
    if (client.signedIn) client.logout(); // e.g. /auth/me failed: do not keep a half-initialized session
    if (error instanceof ApiError && error.code === "VALIDATION_FAILED") {
      for (const [field, messages] of Object.entries(error.errors)) {
        const target = form.querySelector(`[data-error-for="${field.toLowerCase()}"]`);
        if (target) target.textContent = messages.join(" ");
      }
    }
    $("#login-message").textContent = error instanceof ApiError && error.status === 401
      ? "登录失败：用户名或密码不正确，或该用户尚未设置密码（DevelopmentSeed:Password）。"
      : describeError(error);
  } finally {
    button.disabled = false;
    button.textContent = "登录";
  }
});

$("#logout-button").addEventListener("click", () => client.logout());

/* ---------- reads ---------- */

async function loadCatalog() {
  state.catalogStatus = "loading";
  renderCatalog();
  const epoch = client.epoch;
  const [products, locations] = await Promise.all([readAll(client, "/api/v1/products"), readAll(client, "/api/v1/locations")]);
  if (epoch !== client.epoch) return;
  state.products = products;
  state.locations = locations;
  state.catalogStatus = "loaded";
}

async function loadBalances() {
  state.balanceStatus = "loading";
  renderBalances();
  const epoch = client.epoch;
  const filter = { productId: $("#filter-product").value, locationId: $("#filter-location").value };
  const balances = await readAll(client, "/api/v1/inventory/balances", filter);
  if (epoch !== client.epoch) return;
  state.balances = balances;
  state.balanceStatus = "loaded";
  state.loadedAt = new Date();
}

async function runLoad(loader, render) {
  try {
    state.loadError = null;
    await loader();
  } catch (error) {
    if (!client.signedIn || error.code === "SESSION_CHANGED") return;
    state.loadError = error;
    if (state.catalogStatus === "loading") state.catalogStatus = "error";
    if (state.balanceStatus === "loading") state.balanceStatus = "error";
    checkHealth();
  }
  if (client.signedIn) render();
}

// A full load rebuilds the forms (catalog options may change); a balance reload leaves the user's form input alone.
const loadAll = () => runLoad(async () => { await loadCatalog(); await loadBalances(); }, renderAll);
const reloadBalances = () => runLoad(loadBalances, () => { renderSummary(); renderBalances(); });

/* ---------- rendering ---------- */

function renderSummary() {
  const loaded = state.catalogStatus === "loaded";
  $("#product-count").textContent = loaded ? state.products.length : "–";
  $("#location-count").textContent = loaded ? state.locations.length : "–";
  $("#unit-count").textContent = state.balanceStatus === "loaded" ? state.balances.reduce((sum, b) => sum + b.availableQuantity, 0) : "–";
  $("#transaction-count").textContent = state.history.length;
  $("#loaded-at").textContent = state.loadedAt ? `余额读取于 ${formatTime(state.loadedAt)}` : "";
}

function renderFilters() {
  const keep = (select, options) => {
    const current = select.value;
    select.innerHTML = options;
    if ([...select.options].some(o => o.value === current)) select.value = current;
  };
  keep($("#filter-product"), `<option value="">全部商品</option>` + state.products.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.sku)}</option>`).join(""));
  keep($("#filter-location"), `<option value="">全部货位</option>` + state.locations.map(l => `<option value="${escapeHtml(l.id)}">${escapeHtml(l.code)}</option>`).join(""));
}

function statusRow(columns, className, message) {
  return `<tr><td colspan="${columns}" class="${className}">${escapeHtml(message)}</td></tr>`;
}

function renderBalances() {
  const body = $("#inventory-body");
  if (state.balanceStatus === "loading") { body.innerHTML = statusRow(4, "state-loading", "正在读取服务器余额…"); return; }
  if (state.balanceStatus === "error") { body.innerHTML = statusRow(4, "state-error", `读取失败：${describeError(state.loadError)}`); return; }
  if (state.balanceStatus !== "loaded") { body.innerHTML = statusRow(4, "state-empty", "尚未读取。"); return; }
  if (!state.balances.length) {
    body.innerHTML = statusRow(4, "state-empty", "服务器上没有符合条件的库存余额。演示库不预置库存，请先执行一次入库。");
    return;
  }
  body.innerHTML = state.balances.map(b => {
    const product = productById(b.productId);
    return `<tr><td><strong>${escapeHtml(product?.name ?? b.productId)}</strong><small>${escapeHtml(product?.sku ?? "")}</small></td>`
      + `<td>${escapeHtml(locationCode(b.locationId))}</td><td class="number">${b.availableQuantity}</td>`
      + `<td title="${escapeHtml(b.updatedAt)}">${escapeHtml(formatTime(b.updatedAt))}</td></tr>`;
  }).join("");
}

function renderCatalog() {
  const list = $("#product-list");
  if (state.catalogStatus === "loading") { list.innerHTML = `<li class="state-loading">正在读取商品…</li>`; return; }
  if (state.catalogStatus === "error") { list.innerHTML = `<li class="state-error">${escapeHtml(describeError(state.loadError))}</li>`; return; }
  if (!state.products.length) { list.innerHTML = `<li class="state-empty">服务器上没有商品。</li>`; return; }
  list.innerHTML = state.products.map(p => `<li><strong>${escapeHtml(p.sku)} · ${escapeHtml(p.name)}</strong>`
    + `${p.isActive ? "" : ` <span class="pill inactive">已停用</span>`}`
    + `<small>单位 ${escapeHtml(p.unit)} · ${p.capacity == null ? "未设容量" : `容量 ${p.capacity}`} · 条码 ${escapeHtml(p.barcodes.join("、") || "无")}</small></li>`).join("");
}

function renderHistory() {
  $("#history-list").innerHTML = state.history.length ? state.history.map(entry => {
    const lines = entry.result.lines.map(line => {
      const parts = [productName(line.productId), `数量 ${line.quantity}`];
      if (line.from) parts.push(`${locationCode(line.from.locationId)}：${line.from.before} → ${line.from.after}`);
      if (line.to) parts.push(`${locationCode(line.to.locationId)}：${line.to.before} → ${line.to.after}`);
      return parts.join("，");
    }).join("；");
    const extra = [entry.result.reason && `原因：${entry.result.reason}`, entry.result.referenceNo && `单号：${entry.result.referenceNo}`].filter(Boolean).join("，");
    return `<li><span><strong>${escapeHtml(entry.result.transactionNo)} · ${escapeHtml(typeLabels[entry.result.type] ?? entry.result.type)}</strong>`
      + `${entry.replayed ? ` <span class="pill replayed">服务器重放</span>` : ""}<br>${escapeHtml(lines)}${extra ? `<br><small>${escapeHtml(extra)}</small>` : ""}</span>`
      + `<small title="${escapeHtml(entry.result.occurredAt)}">${escapeHtml(formatTime(entry.result.occurredAt))}</small></li>`;
  }).join("") : '<li class="state-empty">本会话尚未成功提交库存操作。</li>';
}

function renderTabs() {
  $("#operation-tabs").innerHTML = Object.entries(operations).map(([key, op]) => {
    const allowed = client.can(op.permission);
    return `<button type="button" role="tab" data-operation="${key}" class="${key === state.activeOperation ? "active" : ""}"`
      + ` aria-selected="${key === state.activeOperation}" title="${allowed ? `需要权限 ${op.permission}` : `当前角色没有 ${op.permission} 权限`}">`
      + `${op.label}${allowed ? "" : " 🔒"}</button>`;
  }).join("");
}

function fieldHtml(name, operation) {
  const error = `<small class="field-error" data-error-for="${name}"></small>`;
  const active = items => items.filter(item => item.isActive);
  if (name === "product") {
    return `<label>${fieldLabels.product}<select name="product">${active(state.products).map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.sku)} · ${escapeHtml(p.name)}</option>`).join("")}</select>${error}</label>`;
  }
  if (name === "location" || name === "fromLocation" || name === "toLocation") {
    const options = active(state.locations).map(l => `<option value="${escapeHtml(l.id)}">${escapeHtml(l.code)}</option>`).join("");
    return `<label>${name === "location" ? operation.locationLabel : fieldLabels[name]}<select name="${name}">${options}</select>${error}</label>`;
  }
  if (numericFields.has(name)) {
    return `<label>${fieldLabels[name]}<input name="${name}" type="number" step="1" inputmode="numeric" value="${name === "countedQuantity" ? 0 : 1}" />${error}</label>`;
  }
  return `<label class="${name === "reason" ? "wide" : ""}">${fieldLabels[name]}<input name="${name}" autocomplete="off" />${error}</label>`;
}

function renderOperationForm() {
  const operation = operations[state.activeOperation];
  const form = $("#operation-form");
  if (state.catalogStatus !== "loaded") { form.innerHTML = `<p class="state-empty">商品和货位读取完成后才能提交操作。</p>`; return; }
  if (!client.can(operation.permission)) {
    form.innerHTML = `<p class="state-error">当前角色（${escapeHtml(roleLabels[client.user?.role] ?? client.user?.role)}）没有 ${escapeHtml(operation.permission)} 权限，不能${operation.label}。请换用有权限的账号登录。</p>`;
    return;
  }
  form.innerHTML = operation.fields.map(name => fieldHtml(name, operation)).join("") + `<button type="submit" id="submit-button">确认${operation.label}</button>`;
  if (operation.fields.includes("toLocation") && form.toLocation.options.length > 1) form.toLocation.selectedIndex = 1;
  renderOperationControls();
}

function renderOperationControls() {
  const submit = $("#submit-button");
  if (submit) {
    submit.disabled = state.busy || state.pending !== null;
    submit.textContent = state.busy ? "提交中…" : `确认${operations[state.activeOperation].label}`;
  }
  const banner = $("#pending-banner");
  if (!state.pending) { banner.classList.add("hidden"); banner.innerHTML = ""; return; }
  const waitSeconds = Math.max(0, Math.ceil((state.retryAt - Date.now()) / 1000));
  banner.classList.remove("hidden");
  banner.innerHTML = `<p><strong>上一次${escapeHtml(operations[state.pending.operation].label)}未确认完成：</strong>${escapeHtml(state.pending.summary)}</p>`
    + `<p class="small">${escapeHtml(state.pending.reason)}</p>`
    + `<p class="small">重试会原样重发同一请求体和同一 Idempotency-Key（${escapeHtml(state.pending.key)}），服务器保证最多执行一次。修改表单或放弃后再提交，将视为新的操作。</p>`
    + `<div class="banner-actions"><button type="button" id="retry-button" ${state.busy || waitSeconds > 0 ? "disabled" : ""}>`
    + `${state.busy ? "重试中…" : waitSeconds > 0 ? `${waitSeconds} 秒后可重试` : "使用相同 Idempotency-Key 重试"}</button>`
    + `<button type="button" class="secondary" id="discard-button" ${state.busy ? "disabled" : ""}>放弃此操作</button></div>`;
}

function renderAll() {
  renderSummary();
  renderFilters();
  renderBalances();
  renderCatalog();
  renderHistory();
  renderTabs();
  renderOperationForm();
}

/* ---------- writes ---------- */

function readForm(operation) {
  const form = $("#operation-form");
  const values = {};
  for (const name of operation.fields) {
    const raw = String(form.elements[name]?.value ?? "").trim();
    if (numericFields.has(name)) values[name] = raw === "" || !Number.isFinite(Number(raw)) ? null : Number(raw);
    else if (name === "referenceNo") values[name] = raw === "" ? undefined : raw;
    else values[name] = raw;
  }
  return values;
}

function summarize(operationKey, body) {
  const line = body.lines[0];
  const amount = line.quantity ?? line.countedQuantity ?? line.quantityDelta;
  const where = body.locationId ? locationCode(body.locationId) : `${locationCode(body.fromLocationId)} → ${locationCode(body.toLocationId)}`;
  return `${operations[operationKey].label} ${productName(line.productId)}，${where}，数量 ${amount}`;
}

function showFieldErrors(errors) {
  const general = [];
  for (const [field, messages] of Object.entries(errors ?? {})) {
    const name = errorFields[field.replace(/^\$\./, "").toLowerCase()];
    const target = name && $(`#operation-form [data-error-for="${name}"]`);
    if (target) target.textContent = messages.join(" ");
    else general.push(`${field}: ${messages.join(" ")}`);
  }
  return general;
}

function clearFieldErrors() {
  document.querySelectorAll("#operation-form [data-error-for]").forEach(el => { el.textContent = ""; });
}

function scheduleRetryCountdown() {
  clearTimeout(state.retryTimer);
  if (state.pending && state.retryAt > Date.now()) {
    state.retryTimer = setTimeout(() => { renderOperationControls(); scheduleRetryCountdown(); }, 1000);
  }
}

async function submitAction(action) {
  if (state.busy) return; // a second click while a request is in flight does nothing
  state.busy = true;
  const epoch = client.epoch;
  renderOperationControls();
  const message = $("#operation-message");
  message.className = "form-message";
  message.textContent = `正在提交：${action.summary}`;
  clearFieldErrors();
  try {
    const { data, replayed } = await client.write(action.path, action.body, action.key);
    if (epoch !== client.epoch) return;
    state.pending = null;
    state.history.unshift({ result: data, replayed });
    renderHistory();
    renderSummary();
    message.className = "form-message success";
    message.textContent = replayed
      ? `服务器已处理过这个操作，返回了原结果 ${data.transactionNo}（Idempotent-Replayed），库存没有再次变化。`
      : `${operations[action.operation].label}已提交：${data.transactionNo}。下方余额已从服务器重新读取。`;
    showToast(`${data.transactionNo} 已提交`);
    const form = $("#operation-form");
    for (const name of ["referenceNo", "reason"]) if (form.elements[name]) form.elements[name].value = "";
  } catch (error) {
    if (epoch !== client.epoch || !client.signedIn) return;
    message.className = "form-message error";
    if (isRetryable(error)) {
      state.pending = { ...action, reason: describeError(error) };
      state.retryAt = Date.now() + (error.retryAfterSeconds ?? 0) * 1000;
      scheduleRetryCountdown();
      message.textContent = "";
    } else {
      state.pending = null;
      const general = error.code === "VALIDATION_FAILED" ? showFieldErrors(error.errors) : [];
      message.textContent = [describeError(error), ...general].join(" ");
    }
  } finally {
    if (epoch === client.epoch) {
      state.busy = false;
      renderOperationControls();
    }
  }
  if (epoch === client.epoch && client.signedIn) await reloadBalances(); // authoritative state after every attempt
}

function discardPending(note) {
  if (!state.pending) return;
  state.pending = null;
  clearTimeout(state.retryTimer);
  const message = $("#operation-message");
  message.className = "form-message";
  message.textContent = `${note}上一次未确认的操作已放弃；它可能已经提交，请先核对上方服务器余额。再次提交会使用新的 Idempotency-Key。`;
  renderOperationControls();
}

$("#operation-form").addEventListener("submit", event => {
  event.preventDefault();
  if (state.busy || state.pending) return;
  const operation = operations[state.activeOperation];
  const body = structuredClone(operation.build(readForm(operation)));
  // One user action = one key. The body is frozen with it, so a retry resends exactly the same request.
  submitAction(Object.freeze({ key: crypto.randomUUID(), operation: state.activeOperation, path: operation.path, body: Object.freeze(body), summary: summarize(state.activeOperation, body) }));
});

$("#operation-form").addEventListener("input", () => {
  if (state.busy) return;
  clearFieldErrors();
  discardPending("输入已修改：");
});

$("#pending-banner").addEventListener("click", event => {
  if (event.target.id === "retry-button" && state.pending && !state.busy && Date.now() >= state.retryAt) submitAction(state.pending);
  if (event.target.id === "discard-button" && !state.busy) discardPending("");
});

$("#operation-tabs").addEventListener("click", event => {
  const button = event.target.closest("button[data-operation]");
  if (!button || state.busy || button.dataset.operation === state.activeOperation) return;
  discardPending("已切换操作：");
  state.activeOperation = button.dataset.operation;
  $("#operation-message").textContent = "";
  renderTabs();
  renderOperationForm();
});

/* ---------- lookup and filters ---------- */

$("#lookup-form").addEventListener("submit", async event => {
  event.preventDefault();
  const value = $("#lookup-value").value.trim();
  const result = $("#lookup-result");
  const button = $("#lookup-button");
  if (!value || button.disabled) return;
  button.disabled = true;
  result.className = "lookup-result";
  result.textContent = "正在查询服务器…";
  const epoch = client.epoch;
  try {
    let product;
    let via;
    try {
      const { data } = await client.get("/api/v1/barcodes/resolve", { value });
      product = data.product;
      via = `条码 ${data.barcode}`;
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 404) throw error;
      product = state.products.find(p => p.sku.toUpperCase() === value.toUpperCase());
      via = "SKU";
    }
    if (epoch !== client.epoch) return;
    if (!product) { result.textContent = "服务器上没有这个条码，已读取的商品中也没有这个 SKU。"; return; }
    const balances = await readAll(client, "/api/v1/inventory/balances", { productId: product.id });
    if (epoch !== client.epoch) return;
    const total = balances.reduce((sum, b) => sum + b.availableQuantity, 0);
    const where = balances.map(b => `${locationCode(b.locationId)}：${b.availableQuantity}`).join("，");
    result.textContent = `按${via}找到：${product.sku} · ${product.name}${product.isActive ? "" : "（已停用）"}。服务器可用库存合计 ${total} ${product.unit}${where ? `（${where}）` : "，目前没有余额"}。`;
    const select = $("#operation-form select[name=product]");
    if (select && !state.pending && !state.busy && [...select.options].some(o => o.value === product.id)) select.value = product.id;
  } catch (error) {
    if (epoch !== client.epoch) return;
    result.className = "lookup-result error";
    result.textContent = error.code === "BARCODE_AMBIGUOUS" ? `该条码对应多个商品（409）。${error.message}` : describeError(error);
  } finally {
    button.disabled = false;
  }
});

$("#filter-product").addEventListener("change", reloadBalances);
$("#filter-location").addEventListener("change", reloadBalances);
$("#reload-button").addEventListener("click", () => { checkHealth(); loadAll(); });

checkHealth();
setInterval(checkHealth, 30000);
$("#login-username").focus();
