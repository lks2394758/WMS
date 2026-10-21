// Real catalog API + SQLite + browser UI acceptance. Run only after B2a is implemented and the API is built.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { deflateSync } from "node:zlib";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

function tinyPng() {
  const chunk = (name, data) => {
    const type = Buffer.from(name), size = Buffer.alloc(4), checksum = Buffer.alloc(4); size.writeUInt32BE(data.length);
    let crc = 0xffffffff;
    for (const byte of Buffer.concat([type, data])) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
    checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0); return Buffer.concat([size, type, data, checksum]);
  };
  const header = Buffer.alloc(13); header.writeUInt32BE(1); header.writeUInt32BE(1, 4); header[8] = 8; header[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", header), chunk("IDAT", deflateSync(Buffer.from([0, 20, 120, 100, 255]))), chunk("IEND", Buffer.alloc(0))]);
}
let total = 0;
async function check(name, action) { await action(); total++; console.log("PASS " + name); }
await withRealV2(async ({ repo, work, base, password, login, request }) => {
  const cookie = await login("supervisor");
  const get = async path => { const response = await request(path, { cookie }); assert.equal(response.status, 200); return response.json(); };
  const write = (method, path, body, key = randomUUID()) => request(path, { method, body, key, cookie });
  const units = await get("units"); assert.ok(units.items.some(x => x.name === "件"));
  const photo = join(work, "product.png"); writeFileSync(photo, tinyPng());
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    const navigate = async (page, title) => {
      await evaluate(`location.hash=${JSON.stringify(page)}`);
      await wait(`document.querySelector('#page-title').textContent===${JSON.stringify(title)} && document.querySelector('#page').getAttribute('aria-busy')==='false'`, title);
    };
    const submit = async () => {
      await evaluate("document.querySelector('#editor-form').requestSubmit()");
      await wait("!document.querySelector('#editor').open && document.querySelector('#page').getAttribute('aria-busy')==='false'", "saved catalog form");
    };
    await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await cdp("Page.navigate", { url: base + "/v2/#products" });
    await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password);
    await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real catalog");
    await check("real browser creates a custom unit and hierarchical categories", async () => {
      await navigate("catalog", "分类与单位");
      await click('[data-action="create"][data-id="units"]'); await wait("document.querySelector('#editor').open", "unit editor");
      await field('#editor-form [name=name]', "千克"); await submit();
      await click('[data-action="create"][data-id="categories"]'); await wait("document.querySelector('#editor').open", "category editor");
      await field('#editor-form [name=name]', "原料"); await submit();
      const parent = (await get("categories")).items.find(x => x.name === "原料"); assert.ok(parent);
      await click('[data-action="create"][data-id="categories"]'); await wait("document.querySelector('#editor').open", "child category editor");
      await field('#editor-form [name=name]', "金属"); await field('#editor-form [name=parentId]', parent.id); await submit();
    });
    let product;
    await check("real browser creates a fractional-unit product with a saved PNG", async () => {
      const unit = (await get("units")).items.find(x => x.name === "千克");
      const category = (await get("categories")).items.find(x => x.name === "金属");
      await navigate("products", "商品与库存"); await click('[data-action="create"][data-id="products"]');
      await wait("document.querySelector('#editor').open", "product editor");
      await field('#editor-form [name=name]', "原型铜材"); await field('#editor-form [name=unitId]', unit.id); await field('#editor-form [name=decimalPlaces]', "3");
      await evaluate(`document.querySelector('#editor-form [name=categoryIds][value="${category.id}"]').checked=true`);
      const { root } = await cdp("DOM.getDocument");
      const { nodeId } = await cdp("DOM.querySelector", { nodeId: root.nodeId, selector: '#editor-form [name=image]' });
      await cdp("DOM.setFileInputFiles", { nodeId, files: [photo] }); await submit();
      product = (await get("products?search=" + encodeURIComponent("原型铜材"))).items[0];
      assert.ok(product && typeof product.id === "string"); assert.equal(product.totalQuantity, "0"); assert.equal(product.decimalPlaces, 3); assert.equal(product.hasImage, true);
      const detail = await get("products/" + product.id); assert.equal(detail.imageContentType, "image/png"); assert.ok(detail.imageBase64); assert.equal(product.imageBase64, undefined);
      await click(`[data-action="detail"][data-id="${product.id}"]`); await wait("document.querySelector('#editor').open && document.querySelector('#editor-fields img')?.naturalWidth>0", "saved real product photo");
      await click('[data-close]'); await wait("!document.querySelector('#editor').open", "closed product detail");
    });
    await check("real product editing preserves its identity and explicitly removes its image", async () => {
      await click(`[data-action="edit"][data-id="${product.id}"]`); await wait("document.querySelector('#editor').open && !!document.querySelector('#editor-form [name=removeImage]')", "existing product editor");
      await field('#editor-form [name=name]', "原型铜材（更名）"); await click('#editor-form [name=removeImage]'); await submit();
      const saved = await get("products/" + product.id); assert.equal(saved.id, product.id); assert.equal(saved.name, "原型铜材（更名）"); assert.ok(!saved.imageBase64);
      assert.ok(saved.version > product.version);
      const parent = (await get("categories")).items.find(x => x.name === "原料");
      assert.ok((await get("products?categoryId=" + parent.id)).items.some(x => x.id === product.id));
      const directory = join(repo, "artifacts", "v2-real-ui"); mkdirSync(directory, { recursive: true });
      const { data } = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(directory, "catalog.png"), Buffer.from(data, "base64"));
    });
    await check("receiving identity has read-only catalog controls in the real UI", async () => {
      await click("#logout"); await wait("!document.querySelector('#login-view').hidden", "signed out");
      await field('#login-form [name=username]', "receiver"); await field('#login-form [name=password]', password);
      await evaluate("document.querySelector('#login-form').requestSubmit()");
      await wait("!document.querySelector('#workspace').hidden && document.querySelector('#page').getAttribute('aria-busy')==='false'", "receiver catalog");
      assert.equal(await evaluate("document.querySelectorAll('[data-action=create],[data-action=edit],[data-action=delete]').length"), 0);
      const receiverCookie = await login("receiver");
      const denied = await request("products", { cookie: receiverCookie, method: "POST", key: randomUUID(), body: { name: "must-not-create" } }); assert.equal(denied.status, 403);
    });
  });
  await check("real product creation replays one row and rejects changed request bodies", async () => {
    const unit = (await get("units")).items.find(x => x.name === "件");
    const body = { name: "防重验证商品", unitId: unit.id, decimalPlaces: 0, serialTracking: false, categoryIds: [] }, key = randomUUID();
    const first = await write("POST", "products", body, key); assert.equal(first.status, 201); const created = await first.json();
    const repeat = await write("POST", "products", body, key); assert.equal(repeat.status, 201); assert.equal(repeat.headers.get("idempotent-replayed"), "true"); assert.equal((await repeat.json()).id, created.id);
    const changed = await write("POST", "products", { ...body, name: "不同正文" }, key); assert.equal(changed.status, 409); assert.equal((await changed.json()).code, "IDEMPOTENCY_CONFLICT");
    assert.equal((await get("products?search=" + encodeURIComponent("防重验证商品"))).items.length, 1);
    const archived = await write("DELETE", "products/" + created.id, { version: created.version }); assert.equal(archived.status, 204);
    const reused = await write("POST", "products", { ...body, sku: created.sku, barcode: created.barcode }); assert.equal(reused.status, 409); assert.equal((await reused.json()).code, "ALREADY_EXISTS");
  });
  await check("Unicode name search is case-insensitive before pagination", async () => {
    const unit = (await get("units")).items.find(x => x.name === "件"), ids = [];
    for (const name of ["écrou alpha", "ÉCROU beta"]) {
      const response = await write("POST", "products", { name, unitId: unit.id, decimalPlaces: 0, serialTracking: false, categoryIds: [] });
      assert.equal(response.status, 201); ids.push((await response.json()).id);
    }
    for (const search of ["ÉCROU", "écrou"]) {
      const query = `products?limit=1&search=${encodeURIComponent(search)}`;
      const first = await get(query); assert.equal(first.items.length, 1); assert.ok(first.nextCursor);
      const second = await get(query + "&cursor=" + encodeURIComponent(first.nextCursor));
      assert.equal(second.items.length, 1); assert.ok(!second.nextCursor);
      assert.deepEqual(new Set([first.items[0].id, second.items[0].id]), new Set(ids));
    }
  });
});
console.log(`PASS ${total} REAL catalog/browser checks; inventory/count workflows are not covered`);
