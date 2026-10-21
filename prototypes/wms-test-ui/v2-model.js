export const roles = { systemAdministrator: "管理员", warehouseSupervisor: "仓库主管", receivingClerk: "收货员", shippingClerk: "发货员" };
export const pages = [
  ["dashboard", "工作台", "01"], ["products", "商品与库存", "02"], ["bins", "库位", "03"],
  ["operations", "出入库操作", "04"], ["counts", "盘点与调查", "05"], ["serialized-items", "实物追踪", "06"],
  ["inventory/transactions", "库存流水", "07"], ["alerts", "异常提醒", "08"], ["activity-logs", "操作历史", "09"],
  ["catalog", "分类与单位", "10"], ["users", "账号管理", "11"], ["settings", "系统设置", "12"]
];
export const types = { receipt: "入库", shipment: "出库", movement: "移库", adjustment: "调整", lowStock: "低位", highStock: "高位", overCapacity: "超容量", countDiscrepancy: "盘点待调查", inProgress: "盘点中", pendingConfirmation: "待确认", completed: "已完成", cancelled: "已取消", open: "未解决", resolved: "已解决" };
Object.assign(types, { pending: "待确认", superseded: "已被后续轮次替代", confirmedNoDifference: "已确认无差异", adjusted: "已调整库存", underInvestigation: "待调查" });
// Matches contracts/v2/README.md; real-backend integration must still validate runtime behavior.
export const forms = {
  products: [
    ["name", "商品名称", "text", true], ["sku", "SKU（留空自动生成）"], ["barcode", "条码（留空自动生成）"],
    ["unitId", "基础单位", "units", true], ["decimalPlaces", "允许小数位数（0～6）", "precision", true],
    ["serialTracking", "启用逐件序列号", "checkbox"], ["categoryIds", "所属分类（可多选）", "categories"],
    ["image", "商品图片（PNG/JPEG，最多 2 MiB）", "file"]
  ],
  bins: [["zone", "库区", "text", true], ["rack", "货架", "text", true], ["level", "层", "text", true], ["cell", "格", "text", true]],
  racks: [["zone", "库区", "text", true], ["rack", "货架", "text", true], ["levels", "层数（留空使用默认）", "integer"], ["cellsPerLevel", "每层格数（留空使用默认）", "integer"]],
  units: [["name", "单位名称", "text", true]],
  categories: [["name", "分类名称", "text", true], ["parentId", "上级分类（不选为顶级）", "parentCategory"]],
  users: [["username", "用户名", "text", true], ["displayName", "姓名", "text", true], ["password", "初始密码（仅新建时填写）", "password", true], ["roles", "可用角色（至少一个）", "roles", true], ["enabled", "允许登录", "checkbox"]],
  settings: [["defaultRackLevels", "新货架默认层数", "integer", true], ["defaultCellsPerLevel", "每层默认格数", "integer", true], ["lowThreshold", "默认低位阈值（%）", "decimal", true], ["highThreshold", "默认高位阈值（%）", "decimal", true], ["sessionHours", "新登录有效时长（小时）", "integer", true]],
  counts: [["binId", "盘点库位", "bins", true]],
  submit: [["actualQuantity", "本轮实盘数量", "decimal", true], ["note", "备注", "textarea"]],
  confirm: [["reason", "确认说明（调整时必填）", "textarea"]],
  cancel: [["reason", "取消原因", "textarea", true]],
  recount: [["reason", "复盘说明", "textarea", true]],
  resolve: [["conclusion", "调查结论", "textarea", true], ["transactionId", "关联实际调整流水 ID（无需调整时留空）"], ["noAdjustmentNeeded", "经核实无需调整库存", "checkbox"]],
  correct: [["serialNumber", "更正后的序列号", "text", true], ["reason", "纠错原因", "textarea", true]],
  "reset-password": [["password", "新密码", "password", true]]
};
export function hasRole(user, ...allowed) { return allowed.includes(user?.currentRole); }
export const manager = user => hasRole(user, "systemAdministrator", "warehouseSupervisor");
export const admin = user => hasRole(user, "systemAdministrator");
export function canOperate(user, type) {
  return manager(user) || (type === "receipts" && hasRole(user, "receivingClerk")) || (type === "shipments" && hasRole(user, "shippingClerk"));
}
export function canManage(user, collection) { return ["users", "settings"].includes(collection) ? admin(user) : manager(user); }
export function listOf(data) { return Array.isArray(data) ? data : data?.items ?? []; }
export function itemName(item) { return item?.path || item?.name || item?.code || item?.serialNumber || item?.username || item?.id || "—"; }
