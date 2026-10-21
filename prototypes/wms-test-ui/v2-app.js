import { createV2Client, actionRequest, escapeHtml as e, decimal, imageDataUrl, describeV2Error } from "/v2-client.js";
import { qrSvg, code128Svg } from "/v2-labels.js";
import { pages, roles, types, forms, manager, admin, canOperate, canManage, listOf, itemName } from "/v2-model.js";

const $ = selector => document.querySelector(selector);
const state = { user: null, page: "dashboard", items: [], units: [], categories: [], products: [], bins: [], busy: false, pending: null, cursor: undefined, nextCursor: null, cursors: [], filters: {}, grid: false, renderEpoch: 0, dialogEpoch: 0, editor: null };
const api = createV2Client({ sessionEnded: () => signedOut("登录已到期、被撤销或被其他登录替代，请重新登录。") });
const fmt = value => value == null || value === "" ? "—" : e(value);
const time = value => value ? e(new Date(value).toLocaleString("zh-CN", { hour12: false })) : "—";
const translated = value => types[value] ?? value;
const badge = value => `<span class="pill ${/low|high|pending|open|Progress/i.test(value) ? "warn" : /over|fail|cancel/i.test(value) ? "bad" : ""}">${fmt(translated(value))}</span>`;
const buttons = actions => `<div class="row-actions">${actions.join("")}</div>`;
const btn = (title, action, id = "", cls = "") => `<button type="button" class="${cls}" data-action="${action}" data-id="${e(id)}">${e(title)}</button>`;
function notice(message, error = false) { const node = $("#notice"); node.hidden = !message; node.className = `notice${error ? " error" : ""}`; node.textContent = message || ""; }
function showError(error) { notice(describeV2Error(error), true); }
function signedOut(message = "") {
  api.invalidate(); state.renderEpoch++; state.user = null; state.pending = null; state.items = []; state.products = []; state.bins = []; state.units = []; state.categories = []; state.editor = null; state.detail = null;
  state.label = null; state.serialPicker = null; state.recentActivity = []; state.dashboardAlerts = []; state.settings = null; state.filters = {}; document.querySelector("body>.print-only")?.remove(); renderPending();
  $("#editor").close(); $("#editor-fields").replaceChildren(); $("#page").replaceChildren(); $("#account-name").textContent = ""; $("#role-select").replaceChildren();
  $("#workspace").hidden = true; $("#login-view").hidden = false; $("#login-error").textContent = message;
}
function adopt(user) {
  if (typeof user?.id !== "string" || !user?.currentRole || !Array.isArray(user.roles) || !user.roles.includes(user.currentRole) || user.roles.some(role => !roles[role])) throw new Error("登录响应缺少有效用户、角色或当前身份；需核对 API v2 合同。");
  state.user = user; $("#account-name").textContent = user.displayName || user.username;
  $("#role-select").innerHTML = user.roles.map(role => `<option value="${e(role)}" ${role === user.currentRole ? "selected" : ""}>${e(roles[role] || role)}</option>`).join("");
  $("#workspace").hidden = false; $("#login-view").hidden = true;
  $("#navigation").innerHTML = pages.filter(([key]) => !["users", "settings"].includes(key) || admin(user)).map(([key, label, number]) => `<a href="#${key}" data-page="${key}"><span>${number}</span>${label}</a>`).join("");
}
async function navigate(reset = true) {
  if (!state.user) return;
  const candidate = location.hash.slice(1) || "dashboard";
  state.page = pages.some(([key]) => key === candidate) ? candidate : "dashboard";
  if (["users", "settings"].includes(state.page) && !admin(state.user)) state.page = "dashboard";
  if (reset) { state.cursor = undefined; state.cursors = []; state.filters = {}; }
  document.querySelectorAll("#navigation a").forEach(a => a.classList.toggle("active", a.dataset.page === state.page));
  $("#page-title").textContent = pages.find(([key]) => key === state.page)?.[1];
  notice(""); await loadPage();
}
function table(headers, rows) {
  return rows.length ? `<div class="table-scroll"><table><thead><tr>${headers.map(s => `<th scope="col">${e(s)}</th>`).join("")}</tr></thead><tbody>${rows.map(row => `<tr>${row.map(cell => `<td>${cell}</td>`).join("")}</tr>`).join("")}</tbody></table></div>` : '<div class="empty">暂无记录<br><span class="small">这里仅显示后端实际返回的数据。</span></div>';
}
function panel(title, body, extra = "") { return `<section class="panel"><div class="panel-head"><h2>${e(title)}</h2>${extra}</div>${body}</section>`; }
function pager() { return `<div class="pager">${btn("上一页", "previous", "", state.cursors.length ? "" : "unavailable")}${btn("下一页", "next", "", state.nextCursor ? "" : "unavailable")}</div>`; }
function optionList(items, selected, empty = "请选择") { return `<option value="">${empty}</option>${items.map(item => `<option value="${e(item.id)}" ${String(item.id) === String(selected) ? "selected" : ""}>${e(itemName(item))}${item.sku ? ` · ${e(item.sku)}` : ""}</option>`).join("")}`; }
async function lookup(path) {
  const items = []; const seen = new Set(); let cursor;
  do {
    const data = await api.get(path, { limit: 200, cursor }); items.push(...listOf(data)); cursor = data?.nextCursor;
    if (cursor && seen.has(cursor)) throw new Error("列表分页重复，请检查后端游标。");
    if (cursor) seen.add(cursor);
    if (seen.size > 100) throw new Error("数据超过选择列表限制，请缩小范围。");
  } while (cursor);
  return items;
}
async function referenceData(resources = ["units", "categories", "products", "bins"]) {
  const epoch = state.renderEpoch;
  const values = await Promise.all(resources.map(lookup));
  if (epoch !== state.renderEpoch || !state.user) return;
  Object.assign(state, Object.fromEntries(resources.map((resource, index) => [resource, values[index]])));
}
async function loadPage() {
  const epoch = ++state.renderEpoch;
  const page = state.page;
  $("#page").setAttribute("aria-busy", "true"); $("#page").innerHTML = '<div class="busy">正在读取真实数据…</div>';
  try {
    if (page === "operations") { await referenceData(); if (epoch !== state.renderEpoch) return; renderOperations(state.operation || "receipts"); return; }
    if (page === "catalog") {
      const [units, categories] = await Promise.all([lookup("units"), lookup("categories")]);
      if (epoch !== state.renderEpoch) return;
      Object.assign(state, { units, categories }); renderCatalog(); return;
    }
    const data = await api.get(page, { limit: 50, cursor: state.cursor, ...(page === "alerts" ? { status: "open" } : {}), ...state.filters });
    if (page === "products") state.categories = await lookup("categories");
    if (epoch !== state.renderEpoch) return;
    if (page === "dashboard") renderDashboard(data);
    else if (page === "settings") { state.settings = data; renderSettings(data); }
    else { state.items = listOf(data); state.nextCursor = data?.nextCursor; renderList(); }
    return true;
  } catch (error) {
    if (epoch !== state.renderEpoch || !state.user) return;
    $("#page").innerHTML = '<section class="panel"><div class="empty">未能读取新版后端数据。<br>没有使用模拟数据或旧版数据替代。</div></section>'; showError(error); return false;
  } finally { if (epoch === state.renderEpoch) $("#page").setAttribute("aria-busy", "false"); }
}
function renderDashboard(data) {
  state.recentActivity = data.recentActivity ?? []; state.dashboardAlerts = data.alerts ?? [];
  const stats = [["商品种类", data.skuCount, "包含未归档的零库存商品"], ["未解决提醒", data.openAlertCount, "低位、高位、超容量与盘点调查"], ["进行中的盘点", data.activeCountCount, "按实际任务状态统计"], ["待调查盘点", data.unresolvedInvestigationCount, "复盘无差异也须明确结案"]];
  $("#page").innerHTML = `<div class="stats">${stats.map(([label, value, hint]) => `<section class="stat"><p>${label}</p><strong>${fmt(value)}</strong><small>${hint}</small></section>`).join("")}</div>
    ${panel("快速查找商品", '<div class="panel-body"><form id="quick-search" class="search-box"><label class="sr-only" for="quick-query">名称、SKU 或条码</label><input id="quick-query" name="search" placeholder="搜索商品名称、SKU 或条码" required><button class="primary">搜索商品</button></form></div>')}
    <div class="two-col">${panel("最近操作", activityTable(data.recentActivity ?? []), '<select id="activity-scope" aria-label="最近操作范围"><option value="all">全仓操作</option><option value="mine">仅自己</option></select>')}${panel("需要关注", alertsTable(data.alerts ?? []), '<a href="#alerts">查看全部 →</a>')}</div>
    <div class="toolbar">${canOperate(state.user, "receipts") ? btn("办理入库", "go-receipt", "", "primary") : ""}${canOperate(state.user, "shipments") ? btn("办理出库", "go-shipment") : ""}${manager(state.user) ? btn("发起盘点", "create", "counts") : ""}</div>`;
  $("#activity-scope").value = data.scope || state.filters.scope || "all";
}
function activityTable(items) { return table(["时间", "操作人 / 身份", "操作", "结果", "详情"], items.map(x => [time(x.occurredAt), `${fmt(x.actorName || x.loginName)}<br><span class="muted">${fmt(roles[x.actorRole] || x.actorRole)}</span>`, fmt(x.description || translated(x.type)), x.success === false ? badge("失败") : badge("成功"), btn("查看", "log-detail", x.id)])); }
function alertsTable(items) { return table(["异常", "商品 / 库位", "时间", "详情"], items.map(x => [badge(x.type), `${fmt(x.productName)}<br><span class="muted">${fmt(x.binCode)}</span>`, time(x.createdAt), btn("查看", "alert-detail", x.id)])); }
function filterBar() {
  const page = state.page;
  const input = (key, label) => `<input name="${key}" value="${e(state.filters[key] || "")}" placeholder="${label}" aria-label="${label}">`;
  const select = (key, label, choices, fallback = "") => `<select name="${key}" aria-label="${label}">${choices.map(([value, text]) => `<option value="${value}"${String(state.filters[key] ?? fallback) === value ? " selected" : ""}>${text}</option>`).join("")}</select>`;
  const fields = page === "alerts" ? "" : `<input name="search" value="${e(state.filters.search || "")}" placeholder="搜索名称、编码或说明" aria-label="搜索">`;
  const localDate = key => { const date = new Date(state.filters[key]); return Number.isNaN(date.getTime()) ? "" : new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16); };
  const dates = ["activity-logs", "inventory/transactions"].includes(page) ? ["from", "to"].map((key, i) => `<input name="${key}" type="datetime-local" value="${localDate(key)}" aria-label="${i ? "结束时间" : "开始时间"}">`).join("") + input("productId", "商品 ID") + input("binId", "库位 ID") : "";
  const logFilters = page === "activity-logs" ? input("actorId", "账号 ID") + input("type", "操作类型") + select("success", "操作结果", [["", "全部结果"], ["true", "成功"], ["false", "失败"]]) : "";
  const alertFilter = page === "alerts" ? select("status", "提醒状态", [["open", "未解决"], ["resolved", "已解决"], ["", "全部"]], "open") + select("type", "提醒类型", [["", "全部类型"], ["lowStock", "低位"], ["highStock", "高位"], ["overCapacity", "超容量"], ["countDiscrepancy", "盘点待调查"]]) + input("productId", "商品 ID") + input("binId", "库位 ID") : "";
  const category = page === "products" ? `<select name="categoryId" aria-label="商品分类">${optionList(state.categories, state.filters.categoryId, "全部分类（含子分类）")}</select>` : "";
  const position = page === "bins" ? ["zone", "rack", "level"].map((key, i) => `<input name="${key}" value="${e(state.filters[key] || "")}" placeholder="${["库区", "货架", "层"][i]}" aria-label="${["库区", "货架", "层"][i]}" maxlength="16">`).join("") : "";
  return `<form id="filter-form" class="toolbar">${fields}${category}${position}${dates}${logFilters}${alertFilter}<button class="secondary">查询</button><button type="button" data-action="clear-filter" class="quiet">清除条件</button></form>`;
}
function renderList() {
  const p = state.page; let headers; let rows;
  const editActions = item => canManage(state.user, p) ? buttons([btn("编辑", "edit", item.id), ...(["products", "bins"].includes(p) ? [btn("归档", "delete", item.id, "danger")] : [])]) : "—";
  if (p === "products") { headers = ["商品 / SKU", "条码", "当前库存", "单位 / 精度", "追踪", "操作"]; rows = state.items.map(x => [ `<strong>${fmt(x.name)}</strong><br><span class="muted code">${fmt(x.sku)}</span>`, fmt(x.barcode), `${fmt(x.totalQuantity)} ${fmt(x.unitName)}`, `${fmt(x.unitName)} / ${fmt(x.decimalPlaces)} 位`, x.serialTracking ? badge("逐件追踪") : "普通商品", buttons([btn("库存分布", "detail", x.id), btn("标签", "label", x.id)]) + editActions(x)]); }
  else if (p === "bins") { headers = ["库位编码", "商品", "库存 / 容量", "占比", "盘点状态", "操作"]; rows = state.items.map(x => [fmt(x.code), fmt(x.productName), `${fmt(x.quantity)} / ${fmt(x.capacity)} ${fmt(x.unitName)}`, x.occupancyPercent == null ? "—" : `${fmt(x.occupancyPercent)}%`, x.frozenCountTaskId ? badge("盘点冻结") : "可操作", buttons([btn("详情", "detail", x.id), btn("标签", "label", x.id)]) + editActions(x)]); }
  else if (p === "counts") { headers = ["任务", "库位", "状态", "发起人", "开始时间", "操作"]; rows = state.items.map(x => [fmt(x.number), fmt(x.binCode), badge(x.status), fmt(x.creatorName), time(x.startedAt), buttons([btn("查看及处理", "detail", x.id)])]); }
  else if (p === "serialized-items") { headers = ["当前序列号", "商品", "所在库位", "状态", "操作"]; rows = state.items.map(x => [`${fmt(x.serialNumber)}${x.matchedHistoricalSerialNumber ? `<br><span class="pill warn">旧号 ${e(x.matchedHistoricalSerialNumber)} 已更正，请使用当前号码</span>` : ""}`, fmt(x.productName), fmt(x.binCode), badge(x.inStock ? "在库" : "不在库"), buttons([btn("历史", "detail", x.id), btn("标签", "label", x.id), ...(manager(state.user) ? [btn("纠错", "correct", x.id)] : [])])]); }
  else if (p === "inventory/transactions") { headers = ["流水编号", "类型", "操作人 / 身份", "时间", "操作"]; rows = state.items.map(x => [fmt(x.number), fmt(translated(x.type)), `${fmt(x.actorName)} / ${fmt(roles[x.actorRole] || x.actorRole)}`, time(x.occurredAt), btn("明细", "detail", x.id)]); }
  else if (p === "users") { headers = ["用户名", "姓名", "已分配角色", "状态", "操作"]; rows = state.items.map(x => [fmt(x.username), fmt(x.displayName), (x.roles ?? []).map(role => e(roles[role] || role)).join(" / "), badge(x.enabled ? "启用" : "停用"), buttons([btn("编辑", "edit", x.id), btn("重置密码", "reset-password", x.id)])]); }
  let body = p === "activity-logs" ? activityTable(state.items) : p === "alerts" ? alertsTable(state.items) : table(headers, rows);
  if (p === "bins" && state.grid) body = binDiagram(state.items);
  let tools = "";
  if (["products", "bins", "users"].includes(p) && canManage(state.user, p)) tools += btn("新增", "create", p, "primary");
  if (p === "bins") tools += btn(state.grid ? "列表视图" : "库位示意", "toggle-grid") + (manager(state.user) ? btn("生成货架", "rack") : "");
  if (p === "counts" && manager(state.user)) tools += btn("开始盲盘", "create", "counts", "primary");
  $("#page").innerHTML = filterBar() + panel(pages.find(([key]) => key === p)?.[1] || "记录", body + pager(), `<div class="toolbar">${tools}</div>`);
  for (const [name, value] of Object.entries(state.filters)) {
    const control = $("#filter-form").elements.namedItem(name);
    if (!control) continue;
    if (["from", "to"].includes(name) && value) {
      const date = new Date(value);
      control.value = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    } else control.value = value;
  }
  $("[data-action=previous]")?.toggleAttribute("disabled", !state.cursors.length); $("[data-action=next]")?.toggleAttribute("disabled", !state.nextCursor);
}
function binDiagram(items) {
  if (!items.length) return '<div class="empty">暂无库位</div>';
  const racks = new Map();
  for (const bin of items) { const key = JSON.stringify([bin.zone, bin.rack]); if (!racks.has(key)) racks.set(key, []); racks.get(key).push(bin); }
  const order = (a, b) => String(a).localeCompare(String(b), "zh-CN", { numeric: true });
  return [...racks.values()].map(bins => {
    const levels = [...new Set(bins.map(b => b.level))].sort(order);
    return `<section class="rack-diagram"><h3>${fmt(bins[0].zone)} 区 / ${fmt(bins[0].rack)} 货架</h3>${levels.map(level => `<div class="rack-level"><span class="level-name">${fmt(level)} 层</span><div class="rack-cells">${bins.filter(b => b.level === level).sort((a, b) => order(a.cell, b.cell)).map(b => `<button class="bin-card ${b.frozenCountTaskId ? "frozen" : ""}" data-action="detail" data-id="${e(b.id)}"><strong>${fmt(b.cell)} 格</strong><span class="small code">${fmt(b.code)}</span><span>${fmt(b.productName)}</span><small>${fmt(b.quantity)} / ${fmt(b.capacity)} ${fmt(b.unitName)}</small>${b.occupancyPercent != null ? `<meter min="0" max="100" value="${Math.max(0, Math.min(100, Number(b.occupancyPercent) || 0))}" aria-label="库位占比 ${e(b.occupancyPercent)}%"></meter>` : ""}${b.frozenCountTaskId ? badge("冻结") : ""}</button>`).join("")}</div></div>`).join("")}</section>`;
  }).join("") + '<p class="small muted diagram-note">示意图显示当前查询页的库位；请按货架筛选或翻页查看其余库位。</p>';
}
function renderCatalog() {
  const block = (key, title) => panel(title, table(key === "categories" ? ["名称", "上级", "操作"] : ["名称", "操作"], state[key].map(x => {
    const cells = [fmt(x.name)]; if (key === "categories") cells.push(fmt(state.categories.find(c => c.id === x.parentId)?.name || "顶级"));
    cells.push(manager(state.user) ? buttons([btn("编辑", `edit-${key}`, x.id), btn("删除", `delete-${key}`, x.id, "danger")]) : "—"); return cells;
  })), manager(state.user) ? btn("新增", "create", key) : "");
  $("#page").innerHTML = `<div class="two-col">${block("categories", "商品分类")}${block("units", "基础单位")}</div><p class="muted small">有引用的单位、有关联商品或子分类的分类不能删除。单位改名不能代替计量换算。</p>`;
}
function renderSettings(data) {
  $("#page").innerHTML = panel("全仓默认设置", `<div class="panel-body"><form id="settings-form"><div class="form-grid">${renderFields(forms.settings, data)}</div><p class="muted small">货架默认值只影响新货架；登录时长只影响新登录；告警默认值立即影响未单独配置的库位。</p><button class="primary">保存设置</button></form></div>`);
}
function renderFields(schema, values = {}) {
  return schema.map(([key, label, kind = "text", required]) => {
    const value = values[key] ?? (key === "decimalPlaces" ? 0 : key === "enabled" ? true : "");
    const attrs = `name="${key}" ${required ? "required" : ""}`;
    let input;
    if (["units", "bins", "parentCategory"].includes(kind)) input = `<select ${attrs}>${optionList(state[kind === "parentCategory" ? "categories" : kind], value, kind === "parentCategory" ? "无（顶级分类）" : "请选择")}</select>`;
    else if (kind === "categories" || kind === "roles") {
      const options = kind === "roles" ? Object.entries(roles).map(([id, name]) => ({ id, name })) : state.categories;
      input = `<div class="check-group">${options.map(item => `<label><input type="checkbox" name="${key}" value="${e(item.id)}" ${[].concat(value).includes(item.id) ? "checked" : ""}>${e(item.path || item.name)}</label>`).join("") || '<span class="muted">暂无分类</span>'}</div>`;
    } else if (kind === "precision") input = `<select ${attrs}>${[0, 1, 2, 3, 4, 5, 6].map(n => `<option ${Number(value) === n ? "selected" : ""}>${n}</option>`).join("")}</select>`;
    else if (kind === "checkbox") input = `<input name="${key}" type="checkbox" ${value ? "checked" : ""}>`;
    else if (kind === "textarea") input = `<textarea ${attrs} maxlength="2000">${e(value)}</textarea>`;
    else if (kind === "file") input = `<input name="${key}" type="file" accept="image/png,image/jpeg">`;
    else input = `<input ${attrs} type="${kind === "password" ? "password" : "text"}" ${["integer", "decimal"].includes(kind) ? `inputmode="${kind === "integer" ? "numeric" : "decimal"}"` : ""} ${kind === "password" ? 'autocomplete="new-password"' : `value="${e(value)}"`} maxlength="${["sku", "barcode", "serialNumber", "name"].includes(key) ? 128 : key === "password" ? 256 : 200}">`;
    return `<label class="${kind === "checkbox" ? "check" : kind === "textarea" || kind === "categories" || kind === "roles" ? "full" : ""}">${e(label)}${required ? " *" : ""}${input}</label>`;
  }).join("");
}
async function collect(schema, form) {
  const data = new FormData(form), result = {};
  for (const [key, , kind = "text", required] of schema) {
    let value = data.get(key);
    if (kind === "file") {
      if (value?.size) {
        if (value.size > 2 * 1024 * 1024 || !["image/png", "image/jpeg"].includes(value.type)) throw new Error("图片必须是最多 2 MiB 的 PNG 或 JPEG。");
        result.imageBase64 = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result.split(",")[1]); reader.onerror = reject; reader.readAsDataURL(value); });
        result.imageContentType = value.type;
      } continue;
    }
    if (["categories", "roles"].includes(kind)) { value = data.getAll(key); if (required && !value.length) throw new Error("请至少选择一个角色。"); }
    else if (kind === "checkbox") value = data.has(key);
    else if (kind === "password") { value = String(value ?? ""); if (required && !value) throw new Error("请填写密码。"); }
    else if (["integer", "precision"].includes(kind)) { if (!required && (value === null || value === "")) value = null; else { if (!/^\d+$/.test(value)) throw new Error("请输入整数。"); value = Number(value); if (!Number.isSafeInteger(value)) throw new Error("整数超出范围。"); } }
    else if (kind === "decimal") value = decimal(value, 6);
    else { value = String(value ?? "").trim(); if (required && !value) throw new Error("请填写所有必填项。"); if (!value) value = null; }
    result[key] = value;
  }
  return result;
}
async function editor(collection, record = null, action = null) {
  if (state.pending) throw new Error("请先重试或明确放弃当前待确认请求。");
  const epoch = state.renderEpoch, dialog = ++state.dialogEpoch;
  await referenceData(action ? [] : collection === "products" ? ["units", "categories"] : collection === "categories" ? ["categories"] : collection === "counts" ? ["bins"] : []);
  if (collection === "products" && record) record = await api.get(`products/${record.id}`);
  if (epoch !== state.renderEpoch || dialog !== state.dialogEpoch || !state.user) return;
  const schema = forms[action || collection];
  if (!schema) throw new Error("该操作尚无表单。");
  state.editor = { collection, record, action, schema };
  $("#editor-form button[type=submit]").hidden = false;
  $("#editor-title").textContent = action ? ({ submit: "提交实盘数量", confirm: "确认盘点结果", cancel: "取消本轮盘点", recount: "开始新一轮复盘", resolve: "提交调查结论", correct: "序列号纠错", "reset-password": "重置账号密码" }[action] || action) : `${record ? "编辑" : "新增"}${({ products: "商品", bins: "库位", users: "账号", categories: "分类", units: "单位", counts: "盘点", racks: "货架" })[collection]}`;
  $("#editor-hint").textContent = collection === "counts" && !action ? "开始后冻结库位。提交前不显示账面数量，结果不自动改变库存。" : "操作由服务端验证，失败不会在浏览器内改写库存。";
  let selectedSchema = record && collection === "users" && !action ? schema.filter(([key]) => key !== "password") : schema;
  if (record && collection === "products" && !action) selectedSchema = [...selectedSchema, ["removeImage", "移除现有商品图片", "checkbox"]];
  state.editor.schema = selectedSchema;
  $("#editor-fields").innerHTML = renderFields(selectedSchema, action ? {} : record || {});
  if (collection === "products") {
    const src = imageDataUrl(record?.imageBase64, record?.imageContentType);
    $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full image-preview" id="product-image-preview">${src ? `<img src="${e(src)}" alt="当前商品图片">` : '<span class="muted">尚未上传商品图片</span>'}</div>`);
  }
  $("#editor-error").textContent = ""; $("#editor").showModal();
}
function renderPending() {
  clearTimeout(state.retryTimer);
  const node = $("#pending"); node.hidden = !state.pending;
  node.innerHTML = state.pending ? `上次请求结果尚未确认。重试会保留原内容及请求编号，不会生成一张新单。${btn("使用原请求重试", "retry", "", "primary")}${btn("停止等待", "abandon", "", "quiet")}<br><span class="small">停止等待不会撤销后端操作，重新提交前请先检查流水。</span>` : "";
  if (state.pending && state.retryAt > Date.now()) {
    const retry = node.querySelector('[data-action="retry"]'); retry.disabled = true;
    retry.textContent = `${Math.ceil((state.retryAt - Date.now()) / 1000)} 秒后可重试`;
    state.retryTimer = setTimeout(renderPending, 1000);
  }
}
async function mutate(request) {
  if (state.busy) return false;
  state.busy = true;
  document.querySelectorAll("button[type=submit], #role-select, #logout").forEach(n => n.disabled = true);
  try {
    await api.send(request.method, request.path, request.body, request.key);
    state.pending = null; renderPending(); $("#editor").close(); $("#editor-form").reset(); state.editor = null;
    const refreshed = await loadPage();
    if (state.user) notice(refreshed === false ? "操作已提交，但刷新数据失败。请先刷新或查看流水，不要重复提交。" : "操作已提交，页面已重新读取后端数据。", refreshed === false);
    return true;
  } catch (error) {
    if (error.retryable && state.user) { state.pending = request; state.retryAt = Date.now() + (error.retryAfterSeconds || 0) * 1000; renderPending(); $("#editor").close(); }
    if (state.user) { showError(error); $("#editor-error").textContent = describeV2Error(error); }
    return false;
  } finally { state.busy = false; document.querySelectorAll("button[type=submit], #role-select, #logout").forEach(n => n.disabled = false); }
}

function renderOperations(selected = "receipts") {
  const operations = [["receipts", "入库"], ["shipments", "出库"], ["movements", "移库"], ["adjustments", "库存调整"]].filter(([type]) => canOperate(state.user, type));
  const type = operations.some(([key]) => key === selected) ? selected : operations[0]?.[0];
  if (!type) { $("#page").innerHTML = '<div class="empty">当前身份没有库存操作权限。</div>'; return; }
  state.operation = type;
  $("#page").innerHTML = `<div class="toolbar tabs">${operations.map(([key, label]) => `<button type="button" data-action="operation" data-id="${key}" class="${key === type ? "selected" : ""}">${label}</button>`).join("")}</div>${panel("整单提交 · 全部成功或全部失败", `<div class="panel-body"><form id="operation-form"><div class="form-grid">${type === "receipts" ? '<label>来源名称<input name="source" maxlength="200"></label>' : type === "shipments" ? '<label>去向说明<input name="destination" maxlength="200"></label>' : ""}${type === "adjustments" ? '<label class="full">调整原因 *<textarea name="reason" required maxlength="2000"></textarea></label>' : ""}</div><div id="operation-lines"></div><div class="toolbar">${btn("＋ 增加一行", "add-line")}<span class="spacer"></span><button class="primary" type="submit">确认整单提交</button></div><p class="muted small">空库位必须重新配置容量；允许超容量但会告警。数量最终以服务端校验为准，不能为负库存。</p></form></div>`)}`;
  if (type === "adjustments") {
    $("#operation-form>.form-grid").insertAdjacentHTML("beforeend", '<label>关联盘点结果 ID（可选）<input name="countResultId" inputmode="numeric" maxlength="19" placeholder="调查差异调整时填写结果 ID"><span class="muted small">关联的是不可变盘点结果，不是任务编号。按当前库存调整，不自动套用旧差额。</span></label>');
  }
  addLine();
}
function addLine() {
  const type = state.operation, index = $("#operation-lines").children.length + 1;
  const element = document.createElement("section"); element.className = "operation-line";
  element.innerHTML = `<h3>操作明细 ${index}${btn("删除行", "remove-line", "", "quiet danger")}</h3><div class="form-grid"><label>商品 *<select name="productId" required>${optionList(state.products, null)}${type === "receipts" ? '<option value="__new">＋ 入库时新增商品</option>' : ""}</select></label>${["shipments", "movements"].includes(type) ? `<label>来源库位 *<select name="fromBinId" required>${optionList(state.bins, null)}</select></label>` : ""}${["receipts", "movements", "adjustments"].includes(type) ? `<label>${type === "adjustments" ? "调整" : "目标"}库位 *<select name="toBinId" required>${optionList(state.bins, null)}</select></label>` : ""}<label>${type === "adjustments" ? "增减数量（减少填负数）" : "基础单位数量"}<input name="quantity" inputmode="decimal" placeholder="序列号商品由实物数决定"></label>${["receipts", "movements", "adjustments"].includes(type) ? '<label>空库位本次容量<input name="capacity" inputmode="decimal" placeholder="已有库存时不填写"></label><label>低位覆盖（%）<input name="lowThreshold" inputmode="decimal" placeholder="留空使用默认值"></label><label>高位覆盖（%）<input name="highThreshold" inputmode="decimal" placeholder="留空使用默认值"></label>' : ""}${type === "receipts" ? '<label>包装单位<input name="packagingUnit" placeholder="如箱；不用换算可留空" maxlength="32"></label><label>包装数量<input name="packagingQuantity" inputmode="decimal"></label><label>每包装折合基础单位<input name="conversionFactor" inputmode="decimal"></label>' : ""}<label class="full serial-field">实物序列号（每行一个；出库/移库填写当前有效号）<textarea name="serialNumbers" placeholder="普通商品留空；追踪商品须指定实际实物"></textarea></label>${type === "receipts" || type === "adjustments" ? '<label>自动生成序列号数量<input name="generateSerialCount" inputmode="numeric" placeholder="与手动序列号二选一"></label>' : ""}</div><div class="inline-product" hidden><h3>本次新建商品</h3><div class="form-grid">${renderFields(forms.products.filter(([key]) => !["categoryIds", "image"].includes(key)))}<label>新增基础单位名称（单位列表没有时填写）<input name="newUnitName" maxlength="32"></label></div></div><p class="line-note">单商品单库位。追踪商品数量由实际实物个数决定，不能用小数。</p>`;
  $("#operation-lines").append(element);
  element.querySelectorAll(".inline-product input, .inline-product select").forEach(n => n.disabled = true);
  if (type !== "receipts") element.querySelector(".serial-field").insertAdjacentHTML("afterend", `<div class="full">${btn("从当前库位选择在库实物", "pick-serials", "", "secondary")}</div>`);
}
function lineValues(line) {
  const value = key => line.querySelector(`[name="${key}"]`)?.value.trim() || null;
  let productId = value("productId");
  const result = { productId: productId === "__new" ? null : productId };
  const product = state.products.find(p => p.id === productId);
  if (productId === "__new") {
    const inline = line.querySelector(".inline-product"); const get = key => inline.querySelector(`[name="${key}"]`)?.value.trim() || null;
    const serialTracking = inline.querySelector("[name=serialTracking]").checked;
    const newUnitName = get("newUnitName");
    result.newProduct = { name: get("name"), sku: get("sku"), barcode: get("barcode"), unitId: newUnitName ? null : get("unitId"), newUnitName, decimalPlaces: Number(get("decimalPlaces")), serialTracking };
    if (!result.newProduct.name || (!result.newProduct.unitId && !result.newProduct.newUnitName)) throw new Error("新增商品需填写名称和基础单位。");
    if (serialTracking && result.newProduct.decimalPlaces !== 0) throw new Error("序列号商品不能使用小数。");
  }
  for (const key of ["fromBinId", "toBinId", "packagingUnit"]) if (value(key)) result[key] = value(key);
  const precision = product?.decimalPlaces ?? result.newProduct?.decimalPlaces ?? 6;
  for (const key of ["quantity", "capacity", "packagingQuantity", "conversionFactor", "lowThreshold", "highThreshold"]) {
    if (value(key)) result[key] = decimal(value(key), key.includes("Threshold") ? 2 : ["quantity", "capacity"].includes(key) ? precision : 6, { positive: !key.includes("Threshold") && !(key === "quantity" && state.operation === "adjustments"), signed: key === "quantity" && state.operation === "adjustments" });
  }
  const serials = value("serialNumbers")?.split(/[\n,，]+/).map(s => s.trim()).filter(Boolean) || [];
  if (serials.length) { if (new Set(serials.map(s => s.toUpperCase())).size !== serials.length) throw new Error("同一行有重复序列号。"); result.serialNumbers = serials; }
  if (value("generateSerialCount")) { const n = Number(value("generateSerialCount")); if (!Number.isSafeInteger(n) || n < 1 || n > 10000) throw new Error("自动序列号数量需为 1～10000。"); result.generateSerialCount = n; }
  if (serials.length && result.generateSerialCount) throw new Error("手动录入与自动生成序列号只能选择一种。");
  if (result.fromBinId && result.fromBinId === result.toBinId) throw new Error("来源和目标库位不能相同。");
  if (!result.quantity && !serials.length && !result.generateSerialCount && !result.packagingQuantity) throw new Error("请填写数量、包装换算或具体实物。");
  return result;
}

async function detail(id) {
  const resource = state.page, epoch = state.renderEpoch, dialog = ++state.dialogEpoch;
  const data = await api.get(`${resource}/${id}`);
  if (!state.user || state.page !== resource || epoch !== state.renderEpoch || dialog !== state.dialogEpoch) return;
  state.detail = data;
  state.editor = null;
  $("#editor-title").textContent = "记录详情"; $("#editor-error").textContent = "";
  $("#editor-hint").textContent = resource === "counts" ? "本轮账面数量在提交前隐藏；历史结果始终保留。" : "以下为服务端实际保存的记录。";
  const labels = { id: "ID", number: "编号", name: "名称", sku: "SKU", barcode: "条码", unitName: "单位", decimalPlaces: "精度", serialTracking: "逐件追踪", totalQuantity: "总库存", code: "库位", productName: "商品", quantity: "数量", capacity: "容量", occupancyPercent: "占用率", serialNumber: "序列号", historicalSerialNumbers: "历史序列号", inStock: "是否在库", binCode: "库位", status: "状态", round: "轮次", startedAt: "开始时间", completedAt: "完成时间", actorName: "操作人", actorRole: "当时角色", occurredAt: "时间", type: "类型", reason: "原因", lines: "流水明细", results: "盘点结果", bins: "库存分布", transactions: "关联流水", history: "历史", source: "来源", destination: "去向" };
  const blind = resource === "counts" && data.status === "inProgress";
  Object.assign(labels, { actorId: "操作人 ID", productId: "商品 ID", binId: "库位 ID", countTaskId: "盘点任务 ID", countResultId: "盘点结果 ID", version: "版本" });
  const sensitive = /token|password|hash|digest|credential/i;
  const handled = resource === "products" ? ["bins"] : resource === "counts" ? ["results", "currentRound"] : resource === "inventory/transactions" ? ["lines"] : resource === "serialized-items" ? ["history", "corrections"] : [];
  const permitted = Object.entries(data).filter(([key]) => !handled.includes(key) && !sensitive.test(key) && !(blind && /currentRound|snapshot|bookQuantity|expectedQuantity|difference/i.test(key)) && !/imageBase64/.test(key));
  const detailValue = (key, value) => {
    if (typeof value === "object" && value !== null) return `<pre>${e(JSON.stringify(value, (k, v) => sensitive.test(k) ? undefined : v, 2))}</pre>`;
    if (key === "actorRole") return fmt(roles[value] || value);
    if (["occurredAt", "startedAt", "completedAt", "createdAt", "updatedAt"].includes(key)) return time(value);
    return fmt(typeof value === "boolean" ? value ? "是" : "否" : translated(value));
  };
  $("#editor-fields").innerHTML = `<dl class="detail-list full">${permitted.map(([key, value]) => `<dt>${e(labels[key] || key)}</dt><dd>${detailValue(key, value)}</dd>`).join("")}</dl>`;
  if (resource === "products") $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>各库位库存</h3>${table(["库位", "数量", "容量", "占比", "盘点冻结"], (data.bins ?? []).map(b => [fmt(b.binCode), `${fmt(b.quantity)} ${fmt(data.unitName)}`, fmt(b.capacity), b.occupancyPercent == null ? "—" : `${fmt(b.occupancyPercent)}%`, b.frozenCountTaskId ? "是" : "否"]))}</div>`);
  if (resource === "counts") {
    const round = data.currentRound;
    if (round) $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>本轮盲盘</h3><p>${fmt(round.binCode)} · ${fmt(round.productName)} · 单位 ${fmt(round.unitName)} · 第 ${fmt(round.round)} 轮</p><p class="muted">本轮开始：${time(round.startedAt)}。提交前不显示本轮账面数量。</p></div>`);
    $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>已提交的盘点结果（历史保留）</h3>${table(["轮次 / 结果 ID", "账面 / 实盘 / 差异", "单位", "处置状态", "关联证据 ID", "提交人 / 身份", "时间"], (data.results ?? []).map(r => [`${fmt(r.round)} / ${fmt(r.id)}`, `${fmt(r.bookQuantity)} / ${fmt(r.actualQuantity)} / ${fmt(r.difference)}`, fmt(r.unitName), badge(r.disposition), `调整流水：${fmt(r.adjustmentTransactionId)}<br>结案记录：${fmt(r.resolutionLogId)}`, `${fmt(r.submittedByName)} / ${fmt(roles[r.submittedByRole] || r.submittedByRole)}`, time(r.submittedAt)]))}</div>`);
  }
  if (resource === "serialized-items") {
    $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>实物流转历史（当时号码）</h3>${table(["流水", "操作", "当时序列号", "来源 → 目标", "操作人 / 身份", "时间"], (data.history ?? []).map(x => [fmt(x.transactionNumber), fmt(translated(x.type)), fmt(x.serialNumber), `${fmt(x.fromBinCode)} → ${fmt(x.toBinCode)}`, `${fmt(x.actorName)} / ${fmt(roles[x.actorRole] || x.actorRole)}`, time(x.occurredAt)]))}<h3>序列号纠错记录（旧号码不可用于库存操作）</h3>${table(["记录 ID", "旧号 → 新号", "原因", "操作人 / 身份", "时间"], (data.corrections ?? []).map(x => [fmt(x.activityLogId), `${fmt(x.previousSerialNumber)} → ${fmt(x.newSerialNumber)}`, fmt(x.reason), `${fmt(x.actorName)} / ${fmt(roles[x.actorRole] || x.actorRole)}`, time(x.occurredAt)]))}</div>`);
  }
  if (resource === "inventory/transactions") $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>不可修改的流水明细</h3>${table(["商品", "数量 / 单位", "来源库位", "目标库位", "来源前 → 后", "目标前 → 后", "当时序列号"], (data.lines ?? []).map(line => [fmt(line.productName), `${fmt(line.quantity)} ${fmt(line.unitName)}`, fmt(line.fromBinCode), fmt(line.toBinCode), `${fmt(line.fromBefore)} → ${fmt(line.fromAfter)}`, `${fmt(line.toBefore)} → ${fmt(line.toAfter)}`, (line.items ?? []).map(x => e(x.serialNumber)).join("，") || "—"]))}</div>`);
  if (resource === "products") {
    const src = imageDataUrl(data.imageBase64, data.imageContentType);
    if (src) $("#editor-fields").insertAdjacentHTML("afterbegin", `<div class="image-preview full"><img src="${e(src)}" alt="商品图片"></div>`);
  }
  if (resource === "counts" && manager(state.user)) {
    let actions = [];
    if (data.status === "inProgress") actions = ["submit", "cancel"];
    if (data.status === "pendingConfirmation") actions = ["confirm", "recount"];
    if (data.status === "completed" && data.hasUnresolvedInvestigation) actions = ["recount", "resolve"];
    if (Array.isArray(data.allowedActions)) actions = data.allowedActions;
    $("#editor-fields").innerHTML += `<div class="toolbar full">${actions.map(action => btn(({ submit: "提交实盘", cancel: "取消本轮", confirm: "确认结果", recount: "复盘", resolve: "调查结案" })[action], `count-${action}`, id)).join("")}</div>`;
  }
  $("#editor-form button[type=submit]").hidden = true; $("#editor").showModal();
}
function closeEditor() { if (state.busy) return; state.dialogEpoch++; $("#editor").close(); state.editor = null; $("#editor-fields").replaceChildren(); $("#editor-form button[type=submit]").hidden = false; }
function showEvidence(kind, id) {
  state.dialogEpoch++;
  const collection = state.page === "dashboard" ? (kind === "log" ? state.recentActivity : state.dashboardAlerts) : state.items;
  const item = collection?.find(x => x.id === id); if (!item) throw new Error("记录已更新，请刷新。");
  state.editor = null; $("#editor-title").textContent = kind === "log" ? "操作记录" : "异常记录";
  $("#editor-hint").textContent = kind === "log" ? "历史证据仅供查看，不允许在这里修改或删除。" : "商品名称与库位编码为当前资料；触发快照和原始说明保留当时记录，不代表当前库存。"; $("#editor-error").textContent = "";
  const pairs = kind === "log" ? [["操作人", item.actorName || item.loginName], ["当时身份", roles[item.actorRole] || item.actorRole], ["时间", time(item.occurredAt)], ["操作", item.description], ["结果", item.success ? "成功" : "失败"], ["失败原因", item.failureReason], ["错误码", item.failureCode]] : [["类型", translated(item.type)], ["状态", translated(item.status)], ["商品", item.productName], ["库位", item.binCode], ["说明", item.message], ["产生时间", time(item.createdAt)], ["解决时间", time(item.resolvedAt)], ["解决说明", item.resolutionNote]];
  $("#editor-fields").innerHTML = `<dl class="detail-list full">${pairs.map(([label, value]) => `<dt>${e(label)}</dt><dd>${fmt(value)}</dd>`).join("")}</dl>`;
  if (kind === "log") {
    $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>关联对象</h3>${table(["对象类型", "编号", "说明"], (item.subjects ?? []).map(x => [fmt(x.type), fmt(x.id), fmt(x.label)]))}<h3>修改前后</h3>${table(["字段", "修改前", "修改后"], (item.changes ?? []).map(x => /password|token|hash|digest|credential/i.test(x.field) ? ["敏感字段", "已隐藏", "已隐藏"] : [fmt(x.field), fmt(x.before), fmt(x.after)]))}</div>`);
  } else if (item.trigger) {
    const names = { quantity: "触发数量", capacity: "容量", occupancyPercent: "占比（%）", lowThreshold: "低位阈值（%）", highThreshold: "高位阈值（%）", unitName: "单位", decimalPlaces: "精度", sku: "SKU", round: "盘点轮次", bookQuantity: "账面数量", actualQuantity: "实盘数量", difference: "差异" };
    $("#editor-fields").insertAdjacentHTML("beforeend", `<div class="full"><h3>触发时快照（不是当前库存）</h3>${table(["字段", "当时记录"], Object.entries(item.trigger).map(([key, value]) => [fmt(names[key] || key), fmt(value)]))}</div>`);
  }
  $("#editor-form button[type=submit]").hidden = true; $("#editor").showModal();
}

async function labelEditor(id) {
  if (state.pending) throw new Error("请先确认待处理请求，再打印当前标签。");
  const resource = state.page, epoch = state.renderEpoch, dialog = ++state.dialogEpoch;
  const data = await api.get(`${resource}/${id}`);
  if (epoch !== state.renderEpoch || dialog !== state.dialogEpoch || !state.user) return;
  const code = resource === "products" ? data.barcode : resource === "bins" ? data.code : data.serialNumber;
  if (!code) throw new Error("后端未返回当前有效编码，不能打印标签。");
  state.label = { code, title: data.name || data.productName || data.code || "实物标签" };
  state.editor = null; $("#editor-title").textContent = "标签预览";
  $("#editor-hint").textContent = "一维条码和二维码使用相同编码；打印不会增加或修改库存。较长编码建议使用二维码。";
  $("#editor-fields").innerHTML = '<label class="full">标签类型<select id="label-format"><option value="qr">二维码</option><option value="code128">一维条码（Code 128）</option></select></label><label>打印份数<input id="label-copies" type="number" min="1" max="100" value="1"></label><div id="label-preview" class="full label-preview"></div><div class="full">' + btn("打印标签", "print-label", "", "primary") + '</div>';
  $("#editor-form button[type=submit]").hidden = true; updateLabelPreview(); $("#editor").showModal();
}
function updateLabelPreview() {
  try {
    const format = $("#label-format").value;
    const svg = format === "qr" ? qrSvg(state.label.code) : code128Svg(state.label.code);
    $("#label-preview").innerHTML = `<strong>${e(state.label.title)}</strong>${svg}<span class="code">${e(state.label.code)}</span>`;
    $("#editor-error").textContent = ""; $("[data-action=print-label]").disabled = false;
  } catch (error) { $("#label-preview").replaceChildren(); $("#editor-error").textContent = error.message; $("[data-action=print-label]").disabled = true; }
}
function printLabel() {
  const copies = Number($("#label-copies").value);
  if (!Number.isInteger(copies) || copies < 1 || copies > 100) throw new Error("打印份数必须为 1～100。");
  document.querySelector("body>.print-only")?.remove();
  const printable = document.createElement("section"); printable.className = "print-only";
  printable.innerHTML = Array.from({ length: copies }, () => `<article class="print-label">${$("#label-preview").innerHTML}</article>`).join("");
  document.body.append(printable); window.addEventListener("afterprint", () => printable.remove(), { once: true }); window.print();
}
async function serialPicker(line) {
  const productId = line.querySelector("[name=productId]").value;
  const binId = line.querySelector("[name=fromBinId], [name=toBinId]").value;
  const product = state.products.find(p => p.id === productId);
  if (!product?.serialTracking || !binId) throw new Error("请先选择启用逐件追踪的商品和来源库位。");
  if (state.pending) throw new Error("请先确认待处理请求。");
  const epoch = state.renderEpoch, dialog = ++state.dialogEpoch;
  const data = await api.get("serialized-items", { productId, binId, inStock: true, limit: 200 });
  if (epoch !== state.renderEpoch || dialog !== state.dialogEpoch || !document.contains(line)) return;
  const items = listOf(data).filter(x => x.inStock && (!x.productId || x.productId === productId) && (!x.binId || x.binId === binId));
  const existing = new Set(line.querySelector("[name=serialNumbers]").value.split(/[\n,，]+/).map(s => s.trim().toUpperCase()));
  state.serialPicker = { line, items }; state.editor = null;
  $("#editor-title").textContent = "选择实际在库实物"; $("#editor-error").textContent = "";
  $("#editor-hint").textContent = data.nextCursor ? "仅显示前 200 件，更多实物可通过扫描或逐行输入当前序列号。最终提交时服务端再次验证位置。" : "只选择本次实际出库或移库的实物。库存调整选择在库实物时，表示减少这些实物。";
  $("#editor-fields").innerHTML = `<div class="full serial-options">${items.map(x => `<label class="check"><input type="checkbox" name="pickedSerial" value="${e(x.id)}" ${existing.has(x.serialNumber.toUpperCase()) ? "checked" : ""}>${e(x.serialNumber)}</label>`).join("") || '<p class="muted">此商品在所选库位没有可选实物。</p>'}</div><div class="full">${btn("使用所选实物", "apply-serials", "", "primary")}</div>`;
  $("#editor-form button[type=submit]").hidden = true; $("#editor").showModal();
}

$("#login-form").addEventListener("submit", async event => {
  event.preventDefault(); const form = event.currentTarget; const button = form.querySelector("button"); button.disabled = true; $("#login-error").textContent = "";
  api.invalidate();
  try { const data = await api.send("POST", "auth/login", { username: form.username.value.trim(), password: form.password.value }); form.password.value = ""; adopt(data.user ?? data); await navigate(); }
  catch (error) { $("#login-error").textContent = `${error.message}${error.status === 404 ? " 新版 API v2 尚未部署或接口未对齐。" : ""}`; }
  finally { form.password.value = ""; button.disabled = false; }
});
$("#logout").addEventListener("click", async () => { if (state.busy || state.pending) { notice("请先确认待处理请求，再退出。", true); return; } try { await api.send("POST", "auth/logout"); signedOut(); } catch (error) { showError(error); } });
$("#role-select").addEventListener("change", async event => {
  const role = event.target.value;
  if (state.pending || state.busy) { event.target.value = state.user.currentRole; notice("请先处理当前请求，再切换身份。", true); return; }
  event.target.disabled = true;
  try { const data = await api.send("POST", "auth/switch-role", { role }); api.invalidate(); adopt(data.user ?? data); await navigate(); }
  catch (error) { if (state.user) { event.target.value = state.user.currentRole; showError(error); } }
  finally { event.target.disabled = false; }
});
$("#refresh").addEventListener("click", () => { if (!state.busy) loadPage(); });
window.addEventListener("hashchange", () => { closeEditor(); navigate(); });
$("#editor").addEventListener("cancel", event => { if (state.busy) event.preventDefault(); else closeEditor(); });
document.addEventListener("change", event => {
  if (event.target.name === "removeImage") {
    const file = $("#editor-form [name=image]"), preview = $("#product-image-preview");
    if (file) { file.disabled = event.target.checked; if (event.target.checked) file.value = ""; }
    if (preview) {
      preview.hidden = event.target.checked;
      const src = imageDataUrl(state.editor?.record?.imageBase64, state.editor?.record?.imageContentType);
      preview.innerHTML = src ? `<img src="${e(src)}" alt="当前商品图片">` : '<span class="muted">尚未上传商品图片</span>';
    }
  }
  if (event.target.id === "label-format") updateLabelPreview();
  if (event.target.type === "file" && event.target.name === "image") {
    const file = event.target.files[0], preview = $("#product-image-preview");
    if (file && preview) {
      if (file.size > 2 * 1024 * 1024 || !["image/png", "image/jpeg"].includes(file.type)) { $("#editor-error").textContent = "请选择最多 2 MiB 的 PNG 或 JPEG 图片。"; event.target.value = ""; return; }
      const reader = new FileReader(); reader.onload = () => {
        if (document.contains(preview)) { const img = document.createElement("img"); img.alt = "待上传商品图片"; img.src = reader.result; preview.replaceChildren(img); }
      }; reader.readAsDataURL(file);
    }
  }
  if (event.target.name === "newUnitName") {
    const unit = event.target.closest(".inline-product")?.querySelector("[name=unitId]");
    if (unit) unit.required = !event.target.value.trim();
  }
  if (event.target.name === "productId") {
    const line = event.target.closest(".operation-line"); if (!line) return;
    const inline = line.querySelector(".inline-product"); inline.hidden = event.target.value !== "__new";
    inline.querySelectorAll("input, select").forEach(n => n.disabled = inline.hidden);
  }
  if (event.target.id === "activity-scope") { state.filters.scope = event.target.value; loadPage(); }
});
document.addEventListener("input", event => {
  if (event.target.name === "newUnitName") {
    const unit = event.target.closest(".inline-product")?.querySelector("[name=unitId]");
    if (unit) unit.required = !event.target.value.trim();
  }
});
document.addEventListener("submit", async event => {
  const form = event.target;
  if (["filter-form", "quick-search", "editor-form", "operation-form", "settings-form"].includes(form.id)) event.preventDefault(); else return;
  try {
    if (form.id === "filter-form") {
      state.filters = Object.fromEntries(new FormData(form)); for (const key of ["from", "to"]) if (state.filters[key]) state.filters[key] = new Date(state.filters[key]).toISOString();
      state.cursor = undefined; state.cursors = []; await loadPage(); return;
    }
    if (form.id === "quick-search") { const search = new FormData(form).get("search"); history.pushState(null, "", "#products"); await navigate(); state.filters = { search }; await loadPage(); return; }
    if (state.pending || state.busy) throw new Error("请先处理当前请求。");
    if (form.id === "editor-form") {
      const current = state.editor; if (!current) return;
      const body = await collect(current.schema, form);
      if (body.removeImage && body.imageBase64) throw new Error("移除图片和上传新图片不能同时进行。");
      if (current.record?.version !== undefined) body.version = current.record.version;
      if (current.action && current.collection === "counts") {
        if (!Number.isInteger(current.record?.round) || !Number.isInteger(current.record?.version)) throw new Error("缺少当前盘点轮次或版本，请刷新任务详情。");
        if (current.record?.round !== undefined) body.round = current.record.round;
        if (current.record?.latestResultId) body.resultId = current.record.latestResultId;
        if (current.action === "confirm" && !body.resultId) throw new Error("没有可确认的最新盘点结果。");
        if (current.action === "resolve" && Boolean(body.transactionId) === Boolean(body.noAdjustmentNeeded)) throw new Error("请关联实际调整流水，或者明确选择无需调整，二者只能选一个。");
      }
      let path = current.collection === "racks" ? "bins/racks" : current.collection;
      if (current.record) path += `/${current.record.id}`;
      if (current.action) path += `/${current.action}`;
      if (body.serialTracking && body.decimalPlaces !== 0) throw new Error("序列号商品的小数位数必须为 0。");
      await mutate(actionRequest(current.record && !current.action ? "PUT" : "POST", path, body));
    }
    if (form.id === "settings-form") { const body = await collect(forms.settings, form); if (state.settings?.version !== undefined) body.version = state.settings.version; await mutate(actionRequest("PUT", "settings", body)); }
    if (form.id === "operation-form") {
      const lines = [...form.querySelectorAll(".operation-line")].map(lineValues); if (!lines.length) throw new Error("至少需要一条明细。");
      const body = { lines }; for (const key of ["source", "destination", "reason"]) if (form.elements[key]?.value.trim()) body[key] = form.elements[key].value.trim();
      if (state.operation === "adjustments" && !body.reason) throw new Error("库存调整必须填写原因。");
      if (state.operation === "adjustments" && form.elements.countResultId?.value.trim()) {
        const id = form.elements.countResultId.value.trim();
        if (!/^[1-9]\d*$/.test(id) || BigInt(id) > 9223372036854775807n) throw new Error("关联盘点结果 ID 必须为有效的正整数。");
        body.countResultId = id;
      }
      await mutate(actionRequest("POST", `inventory/${state.operation}`, body));
    }
  } catch (error) { showError(error); $("#editor-error").textContent = describeV2Error(error); }
});
document.addEventListener("click", async event => {
  const target = event.target.closest("button"); if (!target) return;
  if (target.hasAttribute("data-close")) { closeEditor(); return; }
  const action = target.dataset.action, id = target.dataset.id;
  if (!action || target.disabled) return;
  if (state.busy) return;
  try {
    if (action === "retry") { await mutate(state.pending); return; }
    if (action === "abandon") { if (confirm("这不会撤销后端操作。请先查流水再创建新请求，确定停止等待吗？")) { state.pending = null; renderPending(); await loadPage(); } return; }
    if (action === "next") { state.cursors.push(state.cursor); state.cursor = state.nextCursor; await loadPage(); return; }
    if (action === "previous") { state.cursor = state.cursors.pop(); await loadPage(); return; }
    if (action === "clear-filter") { state.filters = {}; state.cursor = undefined; state.cursors = []; await loadPage(); return; }
    if (action === "toggle-grid") { state.grid = !state.grid; renderList(); return; }
    if (action === "detail") { await detail(id); return; }
    if (action === "log-detail" || action === "alert-detail") { showEvidence(action === "log-detail" ? "log" : "alert", id); return; }
    if (action === "create") { $("#editor-form button[type=submit]").hidden = false; await editor(id); return; }
    if (action === "rack") { await editor("racks"); return; }
    if (action.startsWith("edit")) { const collection = action.split("-")[1] || state.page; const item = (collection === state.page ? state.items : state[collection]).find(x => x.id === id); if (!item) throw new Error("记录已变化，请刷新后重试。"); await editor(collection, item); return; }
    if (action.startsWith("delete")) {
      if (state.pending) throw new Error("请先处理待确认请求。");
      const collection = action.split("-")[1] || state.page;
      const row = (collection === state.page ? state.items : state[collection])?.find(x => x.id === id);
      if (confirm("确定删除或归档？服务端会检查库存、引用和未完成任务；历史记录将保留。")) await mutate(actionRequest("DELETE", `${collection}/${id}`, row?.version === undefined ? {} : { version: row.version })); return;
    }
    if (action === "correct" || action === "reset-password") { await editor(state.page, state.items.find(x => x.id === id), action); return; }
    if (action.startsWith("count-")) { const record = state.detail?.id === id ? state.detail : { id }; closeEditor(); await editor("counts", record, action.slice(6)); return; }
    if (action === "operation") { if (state.pending) throw new Error("请先处理待确认请求。"); renderOperations(id); return; }
    if (action === "add-line") { addLine(); return; }
    if (action === "remove-line") { if ($("#operation-lines").children.length > 1) target.closest(".operation-line").remove(); else notice("至少保留一条明细。", true); return; }
    if (action === "go-receipt" || action === "go-shipment") { history.pushState(null, "", "#operations"); await navigate(); if ($("#operation-form")) renderOperations(action === "go-receipt" ? "receipts" : "shipments"); return; }
    if (action === "label") { await labelEditor(id); return; }
    if (action === "print-label") { printLabel(); return; }
    if (action === "pick-serials") { await serialPicker(target.closest(".operation-line")); return; }
    if (action === "apply-serials") {
      const picker = state.serialPicker; if (!picker || !document.contains(picker.line)) throw new Error("操作表单已改变，请重新选择。");
      const ids = new Set([...document.querySelectorAll('[name=pickedSerial]:checked')].map(n => n.value));
      const selected = picker.items.filter(x => ids.has(x.id)); if (!selected.length) throw new Error("请至少选择一件实物。");
      picker.line.querySelector("[name=serialNumbers]").value = selected.map(x => x.serialNumber).join("\n");
      if (state.operation === "adjustments") picker.line.querySelector("[name=quantity]").value = `-${selected.length}`;
      closeEditor(); state.serialPicker = null; return;
    }
  } catch (error) { showError(error); }
});

// Restore a valid server session, never invent an authenticated user from browser storage.
// Enable login only after its handler exists; the HTML fallback uses POST, never credentials in a URL.
$("#login-form").dataset.ready = "true";
$("#login-form button[type=submit]").disabled = false;
api.get("auth/me").then(data => { adopt(data.user ?? data); return navigate(); }).catch(error => { if (state.user || error.code === "SESSION_CHANGED") return; $("#login-error").textContent = error.status === 401 ? "" : `${error.message} 新版后端尚未就绪时无法登录。`; });
