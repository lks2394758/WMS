# WMS v2 前后端开发交接

更新日期：2026-10-20（America/Vancouver）；下方保留此前各阶段历史。

## 实际状态与责任边界

- 用户已授权：后端由 Claude 开发，前端由 Codex 开发；依据 `docs/database-redesign-decisions.zh-CN.md`。
- 早期两次 Claude Agent 委派因 OAuth 失效未开始；随后用户完成命令行登录，已在正常用户环境验证 loggedIn=true，并通过实际模型响应连通性检查。
- 已通过本机 Claude Code CLI 委派后端。会话 `83b8ea7b-b7ff-4013-8fc9-b92fe4b12654`；仅使用内置文件读写工具，由 Codex 运行构建/测试。B0 至 B5 的本地 SQLite 展示原型已通过自动化验收；当前无后台 Claude 开发进程。尚未完成人工业务验收或生产部署，详见 `docs/v2-prototype-acceptance.zh-CN.md`。
- Codex 已编写本地前端首版，保留旧版 `/`，新版入口 `/v2/`。前端只请求 v2，没有模拟数据回退。
- 不得覆盖旧 `wms-demo.db`；不迁移旧数据；不重置旧账号；不提交、清理或重置用户工作区。
- Claude 可编辑后端 `src/`、`tests/`、`contracts/` 及后端配置/文档；不得编辑 `prototypes/wms-test-ui/`。后者由 Codex 维护。

## 最新恢复点（2026-10-20）

B5b 已完成，展示原型自动化验收通过：后端最终 545（77/12/205/155/96），真实业务/API/页面 62，真实 Kestrel 审计 7。构建 0 警告 0 错误，V2 格式通过。首次全量 544 通过、1 旧断言失败；仅更新 V2AuthApiTests 的超大请求测试为“413、恰好一条安全失败日志、不创建会话”，随后 API 全集 96 通过，未重复其他已通过套件。末次测试修改仅行尾经格式器规范化。生产代码此后未变。

审计：V2RejectionAuditMiddleware 对匹配的变更命令，在框架 400/401/403/413/429 拒绝点写 request-scoped mark，415 用状态识别；业务和登录服务自己的失败不重复记录。安全固定原因、不读正文/凭证/原始路径/查询；401 和认证前 413 不记录身份。审计存储失败保留原响应；独立取消令牌不是硬 5 秒截止。流水线前拒绝和无法走完拒绝处理的断线不能保证记账。真实脚本 `tools/v2-framework-audit-e2e.mjs` 已覆盖声明及分块 413。

本轮未改前端逻辑，按本地验证流程复测所有 7 组真实业务脚本；临时库/浏览器清理完毕，旧库和旧账号未动。此前 51 客户端/代理及 18 合同夹具是历史基线，本轮未重跑，不混计为真实验收。当前无后台 Claude 开发任务或本轮常驻展示服务。下一步是按用户选择启动演示和人工验收；不要擅自初始化旧库、重置账号、安装组件或做生产部署。启动步骤及限制在 `docs/v2-prototype-acceptance.zh-CN.md`。Claude 无可信剩余额度计量，报告 unavailable。

### 历史 B5a 恢复点

B5a 已独立验收，详情 `docs/v2-b5-monitoring.md`：dashboard、alerts、activity-logs 查询及过滤，首页多 SELECT 使用 deferred 一致读快照。541 项后端测试通过（Domain 77/Application 12/Contracts 205/API 92/SQLite 155），构建 0 警告 0 错误，V2 格式通过。前端补待调查数量、提醒类型/商品/库位筛选及筛选条件回显；51 客户端/代理、18 浏览器合同夹具、8 新增真实监控 API/浏览器检查全部通过。真实脚本 `prototypes/wms-test-ui/test/e2e/v2-real-monitoring-browser.mjs`，截图 `artifacts/v2-real-ui/monitoring.png` 已目视检查。旧库和账号未变，临时测试进程已关闭。

复核修正：from 晚于 to 现在 400 VALIDATION_FAILED；时间转 UTC 后比较，from 包含/to 不包含，相等为空集。首页并发快照有时序性压力测试，不保证确定交错、不代表生产数据库验收。提醒 trigger/message 是当时证据；binCode/productName 是当前资料（Alerts 仅存引用），UI 已明确提示。主体过滤对规范化 JSON 的 type+id token 精确子串匹配，无索引；原型可接受。

下一任务仅 B5b：补框架级拒绝失败审计（JSON/模型绑定、策略401/403、限流、请求体限制），禁止凭证入日志、禁止重复业务失败日志；再整体验收和新库独立启动验证。不要宣称生产数据库并发或正式部署完成。Claude 剩余额度 unavailable；继续原会话、low effort、小任务。

### 历史 B4 恢复点

B4a/B4b 已独立验收，覆盖盘点完整生命周期、确认及调查结案；详情 `docs/v2-b4-counts.md`。全量 536 项通过（Domain 77/Application 12/Contracts 205/API 91/SQLite 151），构建 0 警告 0 错误，V2 格式检查通过。新增 `prototypes/wms-test-ui/test/e2e/v2-real-count-browser.mjs` 的 7 项真实 API/浏览器检查全部通过，包括页面确认和结案；原前端表单无需改动。旧库和旧账号未变，测试进程清理完毕。当前无后台 Claude 进程，下一步 B5。

B4b 确认键和摘要存于不可改 countConfirm 成功日志，产生调整时同时存流水；当前身份校验先于重放。普通差异原子调整/流水/提醒/解冻，序列差异不改库存/实物，调查结案覆盖全部未决差异并关联不可改日志。取消轮次不复用；确认可以使用当前 round 加较早 latestResult。独立复核修正：外部详情 task/results/evidence 查询使用 deferred 读事务取得一致快照，不嵌套写事务。并发压力测试为时序性测试，不保证确定交错，也不代表生产数据库并发验收。

节省额度：沿用原 Claude 会话、仅文件工具、小任务和简短回复，`--effort low --autocompact auto`。不要使用 `--autocompact 100k`：本轮出现连续自动压缩、重复读取，已停止该进程后恢复。先聚焦验证，阶段结束全量回归一次。Claude 无法读取可信的账户剩余 token/配额，应报告 unavailable，不得以内部预算冒充。

B5a 查询已完成；下一步仅 B5b 框架级拒绝审计和完整联调，不得扩大为部署或生产数据库改造。

## Claude 恢复后的首要任务

1. 完整阅读 AGENTS.md、README、构建验证脚本、架构约定和重设计定稿，保留所有未提交工作。
2. 先编写 API v2 合同、示例、变更记录、迁移说明及合同测试；告知 Codex 实际请求/响应与以下草案的差异。
3. 按下列阶段实现，使用独立新库 `App_Data/wms-v2-demo.db`。避免启动 v2 时顺带初始化或修改旧库。
4. 每阶段编译及运行聚焦测试；最终报告已实现、未实现、测试、启动方式和新库位置。不得以空实现或假成功代替业务功能。
5. 密码及令牌只走安全配置，不输出或写入代码。不能声称未经目标数据库测试的生产并发安全，不得绕过权限或擅自安装基础设施。

## 分阶段后端任务

| 阶段 | 交付与验收重点 |
| --- | --- |
| B0 合同 | /api/v2；ID 与数量为字符串；清晰 DTO、分页、错误码及请求版本；更新合同示例与测试，不静默改变 v1 |
| B1 持久化与身份 | 按定稿 16 表、新库迁移、独立初始化；多角色账号、单有效会话、切换身份、撤销立即生效；凭证只存哈希 |
| B2 资料与库位 | 商品/图片/历史编码数组、单位/分类、四级库位及货架生成；引用及归档规则、容量/阈值、编码永久占用和编号计数器 |
| B3 库存与实物 | 多行入出移调同事务、防重、精确数量和包装换算、实物状态同步及序列号纠错、不可变流水及日志 |
| B4 盘点与提醒 | 开始时持久快照和冻结、盲盘、不可变结果、复盘及取消状态恢复、调查结案、互斥容量提醒及触发快照 |
| B5 联调与验收 | 首页/搜索/历史过滤、角色权限、重试与并发、重启、旧库隔离、新库初始化；与前端逐项真实联调 |

## 前端接口早期草案（以实际合同为准）

完整资源合同现见 `contracts/v2/README.md`、`src/Wms.Contracts/V2/` 和 `contracts/examples/v2/`。以下保留早期草案供追溯，不再作为实现依据。实际合同明确 version/round 为 JSON 整数，登录态/切换身份为 `{user,expiresAt}`，盘点动作的版本校验不可省略。B1 还须在 Products/CountTasks 中保存创建请求标识和摘要，保证自动编号创建的未知结果重试不会生成重复记录；不另建业务表。

前端 v1 默认代理 5056，v2 默认代理 5057；可用 `WMS_V2_API_BASE_URL` 独立指定新版地址。新版 API 尚未通过验收前，不得将旧 v1 数据作为新版回退。

统一前缀 `/api/v2`。JSON camelCase，时间 UTC，ID 和精确数量是字符串。业务配置整数（精度、层数、时长等）为整数，布尔为布尔。分页采用 `{items,nextCursor}`，查询参数 `limit,cursor`。错误遵循原项目 problem+json，包含稳定 `code` 和字段 `errors`。

库存写入由前端每次用户动作生成 `Idempotency-Key`，重试保持同一正文与编号。其他写入也发送此头，但后端仍须实现其状态转换/自然唯一键防重，不能假设自动覆盖。

### 登录与身份

- `POST auth/login`：`{username,password}`。
- 响应：`{token,expiresAt,user:{id,username,displayName,roles,currentRole,permissions}}`。
- `GET auth/me`：上述 user，或 `{user}`；不得返回令牌或密码哈希。
- `POST auth/switch-role`：`{role}`，返回 user 或 `{user}`，需要换凭证时可返回 token/ expiresAt。
- `POST auth/logout`：撤销当前会话。
- 角色沿用：`systemAdministrator`、`warehouseSupervisor`、`receivingClerk`、`shippingClerk`。
- Node 同源代理将后端凭证封装为 HttpOnly cookie，并从登录 JSON 删除 token/accessToken/refreshToken。后端仍接收 `Authorization: Bearer`。新前端不使用 v1 的自动刷新令牌机制。

### 资料和设置

| 资源 | 请求草案 |
| --- | --- |
| products | GET 列表/`{id}`、POST、PUT `{id}`、DELETE `{id}`；`{name,sku?,barcode?,unitId,decimalPlaces,serialTracking,categoryIds,imageBase64?,imageContentType?,version?}` |
| units | GET、POST、PUT/DELETE `{id}`；`{name,version?}` |
| categories | GET、POST、PUT/DELETE `{id}`；`{name,parentId?,version?}` |
| bins | GET 列表/`{id}`、POST、PUT/DELETE `{id}`；`{zone,rack,level,cell,version?}` |
| bins/racks | POST `{zone,rack,levels,cellsPerLevel}` |
| users | GET 列表/`{id}`、POST、PUT `{id}`；`{username,displayName,roles,enabled,password?,version?}`；POST `{id}/reset-password` 为 `{password}` |
| settings | GET、PUT；`{defaultRackLevels,defaultCellsPerLevel,lowThreshold,highThreshold,sessionHours,version?}` |

列表字段草案：商品 `id,name,sku,barcode,unitId,unitName,decimalPlaces,serialTracking,categoryIds,totalQuantity,version`；库位 `id,code,zone,rack,level,cell,productId,productName,quantity,capacity,unitName,occupancyPercent,frozenCountTaskId,version`。商品详情含 `bins` 库存分布。

商品筛选 `search,categoryId`，上级分类包含后代。归档不等于物理删除。收货员的入库内新增商品/单位走库存行的内嵌请求，不授予一般资料编辑权限。

### 库存

- `POST inventory/receipts|shipments|movements|adjustments`。
- 主体：`{lines,source?,destination?,reason?}`。
- 明细：`{productId?,fromBinId?,toBinId?,quantity?,capacity?,lowThreshold?,highThreshold?,packagingUnit?,packagingQuantity?,conversionFactor?,serialNumbers?,generateSerialCount?,newProduct?}`。
- `newProduct` 仅入库使用，包含商品字段及可选 `newUnitName`；序列号生成与手动号码二选一，数量以服务端所选实物为准。调整减少时 quantity 为负，并指定具体序列号。
- `GET inventory/transactions` 和 `GET inventory/transactions/{id}`。列表 `id,number,type,actorName,actorRole,occurredAt`；详情含 `lines`、快照及前后数量。

### 盘点和调查

- `GET/POST counts`，开始主体 `{binId}`；`GET counts/{id}`。
- 列表/详情：`id,number,binCode,status,creatorName,startedAt,completedAt,round,version,latestResultId,hasUnresolvedInvestigation,results`。
- 状态：`inProgress,pendingConfirmation,completed,cancelled`。开始快照持久保存，但未提交本轮实盘前不通过 API 泄漏账面数量。
- `POST counts/{id}/submit`：`{actualQuantity,note?,round?,version?}`。
- `POST .../confirm`：`{reason?,resultId?,round?,version?}`。
- `POST .../recount|cancel`：`{reason,round?,version?}`。
- `POST .../resolve`：`{conclusion,transactionId?,noAdjustmentNeeded,resultId?,round?,version?}`；服务端列明本次覆盖的所有未决结果，不自动因复盘无差异结案。
- 待确认发起复盘再取消时继续冻结；已完成调查任务发起复盘再取消时恢复完成及原完成时间并解冻。

### 实物、提醒、首页、历史

- `GET serialized-items` 及 `/{id}`，支持当前/旧序列号搜索；记录 `id,serialNumber,historicalSerialNumbers,productName,binCode,inStock,history`。
- `POST serialized-items/{id}/correct`：`{serialNumber,reason}`，仅管理员/主管；旧号保留及查找提示，不能直接用于库存操作。
- `GET alerts`：`status=open|resolved`，列表 `id,type,productName,binCode,createdAt,status`，保留触发快照。
- 告警类型草案：`lowStock,highStock,overCapacity,countDiscrepancy`。
- `GET activity-logs`：支持 `search,from,to,actorId,productId,binId,type,success`；记录 `occurredAt,actorName,actorRole,type,description,success,failureReason`。
- `GET dashboard`：`{skuCount,openAlertCount,activeCountCount,recentActivity,alerts}`；`scope=all|mine` 切换最近操作范围。不返回跨单位相加的总库存。

## 前端现阶段验证与限制

- 客户端/代理自动测试：49 项通过，含原版回归测试、标签参考向量、会话竞态、限流等待、初始化登录 POST 保护及 v1/v2 代理地址隔离（新版离线不得回退旧版）。
- 已有浏览器夹具检查：18 项通过，包括登录保持、编辑请求、旧详情不能覆盖新编辑窗口、失败原样重试、盲盘隐藏与历史结果保留、入库内新增自定义单位、窄屏布局、筛选条件保持、首页操作范围保持、身份权限、标签打印控制、图片显式移除、序列号选择与流转/纠错表格、盘点结果精确 ID 关联调整、只读操作证据的敏感字段隐藏与 HTML 转义、异常触发快照及后端离线提示。这不是 v2 真实 API、数据库或完整业务验收。
- 前端已补充条码/二维码本地编码预览及打印、当前商品图片预览和移除、按商品/库位选择在库实物、按货架分层示意；仍待真实接口联调及物理打印/扫描验证。二维码参考向量测试不等于打印机验收。
- B1 最终独立验收：整方案构建零警告/零错误；后端全量回归 398 项通过（Domain 60、Application 12、Contracts 205、Api 70、Sqlite 51）。其中新版登录 API 13 项、新库/身份存储专项 16 项通过。修正了创建会话时密码重置竞争、提醒及创建元数据保护、锁冲突映射和真实分块请求的 413 错误码；新版源文件空白格式检查通过。
- 前端代理连接真实新版 API 的独立临时数据库检查：`test/e2e/v2-real-auth-e2e.mjs` 7 项通过，覆盖登录/HttpOnly cookie、当前角色、身份切换、API 重启后会话保持、新登录撤销旧会话、退出撤销、真实普通/分块超大请求拒绝及旧库哨兵不变。仅登录链路已真实验证，不代表资料/库存/盘点已实现。
- 不能向用户报告后端开发完成、整体前后端已经接通或全系统已验收；只可明确报告 B1 登录链路已经真实验证。

## 2026-10-19 Claude 额度中断与恢复点

本节是 12:55 的历史中断记录；当天晚间用户明确选择继续由 Claude 开发，已恢复，见下一节。

- B2a 命令已退出（非后台继续运行）：`api_error_status=429`，`api_error=usage_limit_reached`。CLI 提示今天 16:30（America/Vancouver）恢复。不是登录失效，不需要再次发送凭证。
- B2a 目前只写入基础文件：`CatalogPortsV2.cs`、`Common/V2Rules.cs`、`Common/V2Result.cs`、`Common/V2CatalogWriter.cs`、`Catalog/V2ImageValidator.cs`。资料服务、Provider 实现、控制器和专项测试均尚未完成；没有可用的资料 CRUD 端点。部分代码已保留，不回滚或覆盖旧工作。
- 额度中断后的整方案构建仍零警告/零错误；前端 48 项回归通过；后端全量回归再次 398 项通过（60+12+205+51+70）。B2a 基础文件尚未接入实际业务路由，不能将回归通过当作资料功能验收。
- Codex 已准备 `prototypes/wms-test-ui/test/e2e/v2-real-harness.mjs` 和 `v2-real-catalog-browser.mjs`。前者已通过真实临时 API/现有浏览器自检；后者目前仅语法检查，等 B2a 完成才执行，不能当作已通过的商品联调。商品编辑已改为只读取单位/分类，不依赖尚未实现的库位接口。
- 恢复后须先处理图片校验评审：当前 PNG 结构校验可接受没有 IDAT 的 IHDR+IEND、非空 IEND、重复 IHDR 或非法 IHDR 参数；应补明显非法结构检查和正确 CRC 的反例测试。JPEG/PNG 若只做结构验证，不得宣称完整像素解码。真实浏览器脚本生成的是带正确 CRC 和 zlib 数据的合法 1x1 RGBA PNG。
- 继续 B2a 时沿用同一 Claude 会话及仅文件工具模式。Codex 仍负责构建、独立测试和前端；未获用户新授权前不擅自换模型或接管委派的后端。

## 2026-10-19 晚间恢复 B2a（已验收）

- 用户选择继续由 Claude 开发；19:04 后恢复原 CLI 会话，额度已可用。Claude 已返回单位、分类、商品真实 CRUD 的首版，不曾改动冻结初始迁移；无新安装。
- 首版独立构建零警告/零错误；后端全量回归 438 项通过（Domain 60、Application 12、Contracts 205、Api 75、Sqlite 86）。不等于边界规则完整验收。
- 真实前端/Kestrel/临时 SQLite 资料浏览器检查首轮 5 项通过，覆盖自定义单位、分类父子、图片上传/移除、允许小数的商品单位、只读身份、商品创建防重与归档编码占用。新增的 Unicode 搜索与分页检查正在等待修正后执行；不能报告已通过。
- 前端修正了初始化时序：登录按钮默认禁用，绑定处理后才启用；HTML 回退固定 POST，防止脚本尚未就绪时默认把密码送进 URL。前端 49 项回归与 17 项夹具浏览器检查通过。
- 首轮评审要求 Claude 修正：取得写事务后再读取时间、按实际事务内身份记录失败日志、Unicode 名称不分大小写搜索（曾实测 é/É 漏查）、服务层验证失败安全审计，以及已归档目标创建防重重试。修正后的最终结果见下。

### 最终验收结果

- 上述独立评审问题均由 Claude 修正并返回；Codex 核对后再次构建零警告/零错误，V2 源文件和测试空白格式校验通过。
- 最终后端全量 445 项通过：Domain 60、Application 12、Contracts 205、API 76、SQLite 92。新增用可控时钟验证排队跨到期边界、实际角色失败审计、服务层验证拒绝审计、Unicode 双向搜索/分页及归档后同 key 重试。
- 真实资料浏览器联调 6 项通过，真实登录代理回归 7 项通过；测试均使用临时新库和随机内存密码，旧库哨兵未变，不代表已启动/初始化用户的默认新版库。
- 前端最终 49 项回归、18 项浏览器夹具检查通过。补上初始化期间固定 POST/按钮保护，以及旧对话框响应失效机制；真实截图见 `artifacts/v2-real-ui/catalog.png`。
- 服务层验证失败已有安全不可变业务审计；框架解析/模型绑定 400、策略层 401/403、限流及请求体超限目前仍只进请求日志，不进 ActivityLogs。这是后续跨切面补齐项，不能宣称所有失败请求审计已经完整。
- Claude 当前阶段已返回，未留后台开发任务。后续仍由 Claude 负责 B2b（库位/货架、账号、设置）、B3 库存、B4 盘点及 B5 首页/提醒/历史和全流程联调；Codex 继续负责前端和独立验收。

## 2026-10-19 继续 B2b（额度中断，尚未最终验收）

- 用户要求继续，已沿用原 Claude 会话，委派库位/货架、账号、系统设置的真实服务、持久化、控制器和专项测试；仍使用仅文件工具模式，由 Codex 执行构建及独立验收。
- Codex 新增 `prototypes/wms-test-ui/test/e2e/v2-real-admin-browser.mjs`：真实临时 SQLite、Kestrel、前端代理和现有浏览器联调脚本。覆盖库位编辑历史、货架默认规格及冲突整单回滚、Unicode 查询分页、账号页面编辑、密码重置/停用/角色变更撤销会话、最后管理员保护、设置版本冲突、新旧会话时长边界和当前身份权限。脚本已通过语法检查，尚待 B2b 完成后执行，不计入已通过项目。
- 本轮已有回归：前端 49 项、浏览器夹具 18 项、真实登录 7 项、真实资料 6 项通过。真实接口回归运行的是上一轮已构建的 B1/B2a 二进制，不代表 B2b 代码通过构建或验收。
- 旧数据库未改动；临时测试使用随机内存凭证并关闭自有服务/隔离浏览器，无新增依赖或公开发布。

### 本轮实际结果与恢复点

- Claude 已退出，不是后台继续运行：`api_error_status=429`，`api_error=usage_limit_reached`，CLI 提示 `resets 12am (America/Vancouver)`，即当地 2026-10-20 00:00。不是认证失效，不需重新登录；未经用户另行授权不换模型，也不由 Codex 接管后端修正。
- 实际保留的后端文件：`Persistence.Abstractions/V2/AdminPortsV2.cs`、`Sqlite/V2/V2SqliteAdmin.cs`、`Application/V2/Admin/V2BinService.cs`、`V2UserService.cs`、`V2SettingsService.cs`、`Common/V2CapacityAlerts.cs`、`Api/V2/V2AdminControllers.cs`；共享写事务接口/依赖注入/控制器发现同步接入。未新增业务表或修改冻结初始迁移。
- 新增测试：SQLite 的 `V2ServiceTestBase`、库位/账号/设置服务测试共 16 项；API 的 `V2AdminApiTests` 4 项（在首次回归构建之后写入，已重新构建并复测，不能漏算或当成未写入）。
- 最终代码构建零警告/零错误；V2 源文件和测试空白格式只读检查通过。首次回归 461 项通过；最终包含新增 HTTP 测试后 **465 项通过**（Domain 60、Application 12、Contracts 205、API 80、SQLite 108），零失败/零跳过。这不抵消下列已知评审缺口。
- 新增真实前端联调 **9 项通过**：四级库位创建/更名/示意、系统设置及使用新默认生成货架、账号页面新增/无密码编辑、归档碰撞的整架回滚、Unicode 位置查询分页、改名保持会话及重置密码撤销、改角色/停用撤销、最后管理员/设置旧版本保护、当前角色而非角色并集。脚本另验证旧会话到期时间不变，新登录采用新时长。截图 `artifacts/v2-real-ui/accounts.png` 已目视检查。
- 首次脚本把旧编码地址预期写为 400，而 BFF 只开放数字 ID 地址、实际返回 404；修正的是测试预期，未放宽代理。真实脚本重跑全部 9 项通过。旧编码没有被当作当前库位地址。
- 测试用 API/前端代理/隔离浏览器均已结束；没有启动或初始化用户的默认新版库，没有改动旧库。

### 恢复 Claude 后优先处理（尚未发送，额度已阻止继续调用）

1. 统一 `V2BinService.Blocked` 的资料编辑/归档拒绝码与冻结合同：当前实现及自测使用 `BIN_FROZEN`、`INVESTIGATION_UNRESOLVED`，合同错误码表将这两者用于库存操作，而相同资料修改约束为 `OPEN_COUNT_OR_INVESTIGATION`。保留真实任务关联，补 HTTP 断言，不悄悄改合同绕过。
2. 生成货架的成功 ActivityLog 目前仅关联首尾两个库位，遗漏中间库位，不能满足按任意相关 binId 查询操作历史的设计；应关联全部新建库位并验证中间库位证据。设置引起提醒开闭时，同样应检查日志是否关联受影响库位，而非只有设置和提醒 ID。
3. 账号测试名称提到并发，但其中互相停用是顺序调用。补真正排队/并发写入的最后管理员保护测试；只可宣称 SQLite 演示写事务验证，不代表生产数据库并发安全。
4. 补 `docs/v2-b2b-admin.md`、运行状态/变更说明；保持框架模型绑定/策略失败尚待 B5 跨切面审计的已知限制。完成修正后由 Codex 重跑构建、后端全量、9 项真实 B2b、登录/资料回归，再决定最终验收。

## 2026-10-20 恢复尝试：认证阻断

- 用户要求继续，Codex 已向原 Claude 会话提交上述 B2b 评审修正任务，仍限定内置文件工具，不允许命令或前端修改。
- CLI 本次立即退出：`Failed to authenticate: OAuth session expired and could not be refreshed`；本轮模型输入/输出均为 0，没有开始修正。这次是认证失败，不是上一轮的额度提示，不能宣称 Claude 正在后台执行。
- 需要用户在自己的终端执行 `claude auth login` 重新登录后再恢复；不索取、读取或输出凭证，不擅自换模型或由 Codex 接管后端。
- 保留上一轮全部代码、465 项后端通过和 9 项真实 B2b 联调的历史结果；本轮未修改后端代码或数据库，未重复宣称已最终验收。

## 2026-10-20 重新登录后恢复：B2b 最终验收通过

- 用户回复已登录后，原 Claude 会话恢复成功。仅文件工具开发，未换模型；Codex 负责前端和实际构建/测试。Claude 已返回，没有后台任务；本轮没有进入 B3。
- 四项评审全部收尾：资料编辑/归档统一 `OPEN_COUNT_OR_INVESTIGATION` 并返回实际任务/库位关联；货架日志关联每个新库位（含 10000 格），设置日志关联改变的提醒及库位/商品；管理员实际排队和并发会话复核测试；补全 `docs/v2-b2b-admin.md` 和运行状态/变更说明。无新增表或初始迁移改动。
- 首轮 471 项中 470 通过、1 项因测试 SQL 中 ActivityLogs/json_each 的 Type 重名失败。Codex 将失败交回 Claude；Claude 修正表别名，保留完整证据及一万格断言，另为闸门测试补 finally 释放和有界等待。没有跳过或放宽失败用例。
- 最终构建零警告/零错误，V2 空白格式只读检查通过。后端 **471 项全部通过**（Domain 60、Application 12、Contracts 205、API 81、SQLite 113），零失败/零跳过。只证明 SQLite 演示环境，不代表生产数据库并发验收。
- 真实临时 API/SQLite/前端代理联调：登录 **7**、资料 **6**、库位账号设置 **10** 项通过。旧库哨兵保持不变；截图 `artifacts/v2-real-ui/accounts.png` 已目视检查。
- Codex 补齐前端错误关联对象显示：保留精确字符串 ID，以 textContent 显示冲突库位；可见摘要最多十项而原始证据不截断；拒绝后保留表单。新增第十项真实浏览器验证及两项客户端测试。前端 **51** 项、浏览器夹具 **18** 项通过。按 Sites 本地流程验证，未安装框架或发布。
- 所有自有测试 API/代理/隔离浏览器已关闭；未启动/初始化用户默认新版库，未修改旧库或重置账号，未提交、清理或重置工作区。
- 下一阶段由 Claude 开发 B3 真实库存操作（入库/出库/移库/调整、换算、防重、序列号与不可变流水），Codex 负责前端联调和独立验收。B4 盘点、B5 首页/提醒/历史及框架级失败审计仍未完成；不宣称整体系统完成。

## 2026-10-20 B3 开始（进行中，未验收）

- 用户明确要求执行下一步，且结束时让 Claude 报告 token 剩余用量。任务已交给原 Claude 会话，要求区分可见上下文预算与账号订阅/限流剩余额度；无法获取时明确不可用，不从窗口大小或累计消耗推算账号余额，不读取认证文件取数。
- 为便于独立验收，分 B3a（四类库存命令、序列号库存变更、不可变流水查询）和 B3b（实物查询/历史/纠错）执行；先完成和验证核心，再接下一步。仍是 Claude 后端、Codex 前端和实际测试，仅文件工具模式，不安装或修改旧库。
- Codex 新增 `prototypes/wms-test-ui/test/e2e/v2-real-inventory-browser.mjs`：准备真实临时库/API/浏览器的换算、整单回滚、inline 新资料回滚、防重并发重试、不可变快照、权限及实物 ID 回库检查。仅语法检查通过，等待真实后端完成后执行，未计为验收结果。
- 前端已修正操作成功刷新后自动跳回入库的问题，保留当前出库/移库/调整模式并受当前身份权限约束。现有 51 项前端回归、18 项浏览器夹具通过，夹具补断言出库和调整成功后不跳页；这不是 B3 真实业务验收。
- B3a 首版核心接口已可运行；Codex 独立真实库存/API/浏览器 **15 项通过**，含同 key 并发重放、并发超卖拒绝、Unicode 流水分页、inline 新资料整单回滚、精度及换算、库存模式保持与拒绝表单保留。截图 `artifacts/v2-real-ui/inventory.png` 已目视检查，详情身份/时间已改成中文和本地时间展示。旧流水证据没有改写。
- 开发过程已构建版本的后端 **488 项通过**（Domain 77、Application 12、Contracts 205、API 81、SQLite 113）。之后新写入 `V2InventoryServiceTests` 时发现三处 CS1503（290/293/308 行把 InventoryLine 传给需要 InventoryOperationRequest 的 ExecuteAsync）；尚待 Claude 修复，不把旧构建结果冒充最新代码通过。
- 初步独立评审待修：生成序列号须统计整单手工加自动总量上限 10000；`BIN_FROZEN` 拒绝需保留关联实际任务 ID。上述问题与测试编译错误待当前 Claude 返回后交回。B3a、B3b 均尚未最终验收。
- Codex 另准备 `test/e2e/v2-real-serial-browser.mjs` 用于 B3b 的旧号查找/禁用、纠错不可变证据、实物连续历史和真实序列号选择器；仅语法检查，尚未执行，不计为通过。

### B3a 独立验收通过，B3b 已委派

- Claude 首版返回后，Codex 将三处测试 CS1503、整单手工/自动序列号总量限制和冻结任务 subject 缺失交回；均由 Claude 修正，并补四项纯解析边界、HTTP 总量拒绝/任务关联和事务无副作用检查。
- 最终 B3a 构建零警告/零错误；后端 **511 项通过**（Domain 77、Application 12、Contracts 205、API 85、SQLite 132），零失败/零跳过。新版格式验证通过；仅对新领域测试文件做了一次机械行尾规范化，未改业务代码或无关文件。
- 真实库存/API/浏览器 **15 项重跑通过**；前端 **51**、浏览器夹具 **18** 项通过。临时库/旧库哨兵隔离，未改实际演示库或账号；所有自有临时进程测试结束清理。
- Claude 首次报告的内部 token 计数不代表实际上下文剩余或账号配额，Codex 已要求纠正。Claude 已明确更正：实际剩余上下文、账号订阅/限流额度均无可验证数据；不得拿内部预算计数、最大窗口或缓存累计值代替。
- 现已继续委派 B3b（真实实物查询、历史、号码纠错），仍是原 Claude 会话和仅文件工具模式；返回时须再次报告可验证的剩余额度或明确不可获取。B3b 未完成前，不宣称整个 B3 验收完成。

### B3b 额度中断与最终状态

- 原 Claude 会话在 B3b 开始后返回 `api_error=usage_limit_reached`，CLI 提示 `resets 4:20pm (America/Vancouver)`，即当地 **2026-10-20 16:20**。进程已退出，不是后台开发；不擅自换模型或由 Codex 接管委派后端，也未创建定时唤醒。
- B3b 实际保留：`InventoryPortsV2.cs` 中实物查询/历史/纠错端口记录，`CatalogPortsV2.cs` 并入 `IV2SerialWriteOperations`，`Sqlite/V2/V2SqliteSerials.cs` Provider，库存 Item 映射共享及 DI 注册。没有应用服务、Controller、专项测试或 `docs/v2-b3-serials.md`；公开 `/serialized-items` 端点仍未实现。不得将 Provider 完成当作 B3b 完成。
- 中断后重新构建零警告/零错误；最终后端 **511 项全通过**，新版空白格式检查通过。真实回归 **38 项通过**（登录 7、资料 6、库位账号设置 10、库存 15）；前端 51、浏览器夹具 18 项在本轮最终前端修改后已通过。旧库哨兵均未变，所有自有临时 API/代理/隔离浏览器已关闭。
- 用户要求的用量报告：Claude 已明确纠正首次不可靠数字，实际剩余上下文、账号订阅/限流额度没有可验证的 token 数；本次 CLI 只报告“已触发使用上限、16:20 恢复”。不把最大上下文、内部预算计数、累计缓存/输入当作账号剩余额度。
- 恢复后沿用原会话继续 B3b，先核对已有 Provider，不重复覆盖；补查询/纠错用例、薄 Controller、SQLite/HTTP 测试和阶段文档，再由 Codex 执行 `v2-real-serial-browser.mjs` 与全量回归。核对全局当前/历史唯一、no-op/版本、实际身份与不可变流水快照；B4/B5 未开始。

### 用户确认额度重置后，B3b 恢复（尚待最终验收）

- 用户确认用量重置后，Codex 已恢复原 Claude 会话，继续使用仅文件工具模式。序列号应用服务、薄 Controller 及路由已写入；SQLite 和 HTTP 专项测试、文档正在补齐。
- 首版构建零警告/零错误。已构建测试版本 **518 项通过**（77+12+205+85+139）；此时新 HTTP 序列号测试尚未包含在该次构建，不能当作最终总数。
- 真实序列号脚本扩展到 **9 项**：当前/旧号查询、精确筛选、Unicode 分页、纠错证据、无库存改变、旧号禁止操作、实际出库选择器等。当前 **8 通过、1 失败**：同号码但旧版本请求返回 200，独立检查按通用版本约定预期 409；须交回 Claude 核对/修正后重跑，不计为已验收。脚本收集全部失败后整体以非零退出，未跳过或弱化断言。
- 前端基线 **51**、浏览器夹具 **18** 项通过；新增真实截图 `artifacts/v2-real-ui/serials.png` 已检查，脚本已补桌面尺寸。旧库未改动。

### B3b 最终独立验收通过（B3 完成，B4 尚未开始）

- Claude 返回序列号服务、Controller、7 项 SQLite 专项、3 项 HTTP 专项及 `docs/v2-b3-serials.md`，沿用已有 Provider，未新增迁移/表。Codex 独立评审发现“相同号码的旧版本请求”绕过版本检查，交回原会话修正；Claude 已把版本核验置于 no-op 之前，并同步服务/HTTP/真实并发断言及说明。冻结公共版本规则未变：旧版本一律 409，当前版本同号码才 200 且不增加成功日志/历史/版本。
- 最终构建零警告/零错误；新版源文件和测试空白格式检查通过。后端 **521 项全通过**（Domain 77、Application 12、Contracts 205、API 88、SQLite 139），零失败/零跳过。只证明临时 SQLite，不代表生产数据库并发验收。
- 真实序列号/API/浏览器 **9 项全通过**，包括当前/旧号精确筛选、Unicode 搜索后分页、纠错不可变证据、无库存改变、旧号禁止操作和当前号码关联、版本/no-op、全局占用、物理 ID 连续回库、收货员只读、页面纠错及实际库位实物选择器。首轮 8 通过/1 失败的断言保留并在修正后重跑，未跳过。
- 完整真实 API 回归 **47 项通过**（登录 7、资料 6、库位账号设置 10、库存 15、序列号 9），前端 **51**、浏览器夹具 **18** 项通过。截图 `artifacts/v2-real-ui/serials.png` 已按桌面尺寸目视检查；旧号警告、当前号码和事件时号码分开显示，错误关联对象补充中文“实物/流水/盘点结果”等标签，精确 ID 仍是字符串。
- 所有测试使用随机内存凭证/临时新库/隔离浏览器；旧库哨兵未变，没有初始化用户默认新库或重置账号；自有 API/代理/浏览器已清理。没有安装、发布、提交或清理用户工作区。
- Claude 最终用量报告：无法获取可信的实际剩余上下文、账号订阅/限流额度；具体剩余 token 数不可用。本轮 Claude 已正常返回，无后台任务，也未创建定时唤醒。
- 下一步由 Claude 开发 B4 盘点/盲盘/冻结/确认/复盘/调查结案，Codex 继续前端与独立验收；B5 查询及框架级失败审计仍待完成。不要把 B3 验收等同于整个重设计系统完成。
