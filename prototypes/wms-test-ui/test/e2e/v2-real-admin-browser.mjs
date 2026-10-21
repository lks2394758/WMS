// B2b acceptance: real Kestrel + temporary SQLite + BFF + existing browser. No seeded business fixtures.
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

let total = 0;
async function check(name, action) { await action(); total++; console.log("PASS " + name); }
await withRealV2(async ({ repo, work, base, password, login, request }) => {
  const supervisor = await login("supervisor");
  const originalExpiry = (await (await request("auth/me", { cookie: supervisor })).json()).expiresAt;
  let adminCookie;
  const get = async (path, cookie = supervisor) => {
    const response = await request(path, { cookie }); assert.equal(response.status, 200); return response.json();
  };
  const write = (method, path, body, cookie = supervisor) => request(path, { method, body, cookie });
  const problem = async (response, status, code) => {
    assert.equal(response.status, status); assert.equal((await response.json()).code, code);
  };
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    const navigate = async (page, title) => {
      await evaluate(`location.hash=${JSON.stringify(page)}`);
      await wait(`document.querySelector('#page-title').textContent===${JSON.stringify(title)} && document.querySelector('#page').getAttribute('aria-busy')==='false'`, title);
    };
    const submit = async () => {
      await evaluate("document.querySelector('#editor-form').requestSubmit()");
      await wait("!document.querySelector('#editor').open && document.querySelector('#page').getAttribute('aria-busy')==='false'", "saved B2b form");
    };
    await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await cdp("Page.navigate", { url: base + "/v2/#bins" });
    await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password);
    await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real bin list");
    await check("real browser creates, edits and draws four-level bins", async () => {
      await click('[data-action=create][data-id=bins]'); await wait("document.querySelector('#editor').open", "bin editor");
      for (const [key, value] of Object.entries({ zone: "A", rack: "B", level: "C", cell: "04" })) await field(`#editor-form [name=${key}]`, value);
      await submit();
      const created = (await get("bins?search=A-B-C-04")).items[0]; assert.ok(created); assert.equal(created.quantity, "0"); assert.equal(created.capacityStatus, "notConfigured");
      await click(`[data-action=edit][data-id="${created.id}"]`); await wait("document.querySelector('#editor').open", "edit bin");
      await field('#editor-form [name=cell]', "05"); await submit();
      const saved = await get("bins/" + created.id); assert.equal(saved.code, "A-B-C-05"); assert.ok(saved.historicalCodes.includes("A-B-C-04"));
      await click('[data-action=toggle-grid]'); await wait("!!document.querySelector('.rack-diagram .bin-card')", "real rack diagram");
      assert.ok(await evaluate("document.querySelector('.rack-diagram').textContent.includes('A-B-C-05')"));
    });
    await check("real browser saves versioned settings and generates a rack from new defaults", async () => {
      await navigate("settings", "系统设置");
      for (const [key, value] of Object.entries({ defaultRackLevels: "2", defaultCellsPerLevel: "3", lowThreshold: "15.25", highThreshold: "85.75", sessionHours: "2" })) await field(`#settings-form [name=${key}]`, value);
      await evaluate("document.querySelector('#settings-form').dataset.beforeSave='true';document.querySelector('#settings-form').requestSubmit()");
      await wait("!!document.querySelector('#settings-form') && !document.querySelector('#settings-form').dataset.beforeSave && document.querySelector('#page').getAttribute('aria-busy')==='false'", "saved settings");
      const saved = await get("settings"); assert.equal(saved.defaultRackLevels, 2); assert.equal(saved.defaultCellsPerLevel, 3); assert.equal(saved.lowThreshold, "15.25"); assert.equal(saved.sessionHours, 2);
      await navigate("bins", "库位"); await click('[data-action=rack]'); await wait("document.querySelector('#editor').open", "rack editor");
      await field('#editor-form [name=zone]', "展示区"); await field('#editor-form [name=rack]', "R1"); await submit();
      const bins = (await get("bins?zone=" + encodeURIComponent("展示区") + "&rack=R1")).items;
      assert.equal(bins.length, 6); assert.equal(bins[0].code, "展示区-R1-01-01"); assert.equal(bins.at(-1).code, "展示区-R1-02-03");
      assert.ok(bins.every(x => x.effectiveLowThreshold === "15.25" && x.effectiveHighThreshold === "85.75"));
    });
    await check("real browser names conflicting bins and keeps the rejected rack form open", async () => {
      await click('[data-action=rack]'); await wait("document.querySelector('#editor').open", "duplicate rack editor");
      await field('#editor-form [name=zone]', "展示区"); await field('#editor-form [name=rack]', "R1");
      await evaluate("document.querySelector('#editor-form').requestSubmit()");
      await wait("document.querySelector('#editor-error').textContent.includes('ALREADY_EXISTS')", "real rack conflict");
      assert.ok(await evaluate("document.querySelector('#editor').open && document.querySelector('#editor-error').textContent.includes('展示区-R1-01-02')"));
      assert.equal(await evaluate("document.querySelector('#editor-form [name=zone]').value"), "展示区");
      assert.equal((await get("bins?zone=" + encodeURIComponent("展示区") + "&rack=R1")).items.length, 6);
      await click('[data-close]'); await wait("!document.querySelector('#editor').open", "close conflict");
    });
    await check("real browser creates and edits an account without resending its password", async () => {
      await navigate("users", "账号管理"); await click('[data-action=create][data-id=users]'); await wait("document.querySelector('#editor').open", "user editor");
      await field('#editor-form [name=username]', "demo.clerk"); await field('#editor-form [name=displayName]', "展示操作员"); await field('#editor-form [name=password]', password);
      await click('#editor-form [name=roles][value=receivingClerk]'); await submit();
      assert.ok(await evaluate("document.querySelector('#page').textContent.includes('展示操作员')"));
      await evaluate("[...document.querySelectorAll('tr')].find(row=>row.textContent.includes('demo.clerk')).querySelector('[data-action=edit]').click()");
      await wait("document.querySelector('#editor').open && !!document.querySelector('#editor-form [name=displayName]')", "existing account editor");
      assert.equal(await evaluate("document.querySelectorAll('#editor-form [name=password]').length"), 0);
      await field('#editor-form [name=displayName]', "展示操作员（页面更名）"); await submit();
      assert.ok(await evaluate("document.querySelector('#page').textContent.includes('展示操作员（页面更名）')"));
      // This separate admin login intentionally happens only after the browser account edits are finished.
      const directory = join(repo, "artifacts", "v2-real-ui"); mkdirSync(directory, { recursive: true });
      const { data } = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(directory, "accounts.png"), Buffer.from(data, "base64"));
    });
  });
  adminCookie = await login("admin");
  const adminGet = path => get(path, adminCookie);
  await check("partial rack collisions roll back every generated bin, including archived collisions", async () => {
    const body = { zone: "原子区", rack: "R", level: "02", cell: "02" };
    const created = await write("POST", "bins", body); assert.equal(created.status, 201); const bin = await created.json();
    assert.equal((await write("DELETE", "bins/" + bin.id, { version: bin.version })).status, 204);
    await problem(await write("POST", "bins/racks", { zone: body.zone, rack: body.rack, levels: 2, cellsPerLevel: 2 }), 409, "ALREADY_EXISTS");
    assert.equal((await get("bins?zone=" + encodeURIComponent(body.zone))).items.length, 0);
    await problem(await write("POST", "bins", body), 409, "ALREADY_EXISTS");
    // BFF routes accept IDs only: historical printed codes must never address a current bin.
    assert.equal((await request("bins/A-B-C-04", { cookie: supervisor })).status, 404);
  });
  await check("Unicode position filters and search run before cursor pagination", async () => {
    const ids = [];
    for (const cell of ["01", "02"]) {
      const response = await write("POST", "bins", { zone: "école", rack: "R", level: "01", cell }); assert.equal(response.status, 201); ids.push((await response.json()).id);
    }
    for (const search of ["ÉCOLE", "école"]) {
      const query = `bins?limit=1&zone=${encodeURIComponent("ÉCOLE")}&search=${encodeURIComponent(search)}`;
      const first = await get(query); assert.equal(first.items.length, 1); assert.ok(first.nextCursor);
      const second = await get(query + "&cursor=" + encodeURIComponent(first.nextCursor)); assert.equal(second.items.length, 1); assert.ok(!second.nextCursor);
      assert.deepEqual(new Set([first.items[0].id, second.items[0].id]), new Set(ids));
    }
  });
  await check("account metadata refreshes without revocation; password reset revokes immediately", async () => {
    let user = (await adminGet("users?search=demo.clerk")).items[0]; assert.ok(user);
    const cookie = await login("demo.clerk");
    const edited = await write("PUT", "users/" + user.id, { username: user.username, displayName: "展示操作员更名", roles: user.roles, enabled: true, version: user.version }, adminCookie);
    assert.equal(edited.status, 200); user = await edited.json();
    const me = await get("auth/me", cookie); assert.equal(me.user.displayName, user.displayName);
    const hoursRemaining = (Date.parse(me.expiresAt) - Date.now()) / 3600000;
    assert.ok(hoursRemaining > 1.95 && hoursRemaining <= 2.01);
    assert.equal((await get("auth/me", supervisor)).expiresAt, originalExpiry);
    assert.ok(!Object.keys(user).some(key => /password|hash|token/i.test(key)));
    const replacement = randomBytes(24).toString("base64url");
    const reset = await write("POST", "users/" + user.id + "/reset-password", { password: replacement, version: user.version }, adminCookie); assert.equal(reset.status, 200);
    await problem(await request("auth/me", { cookie }), 401, "SESSION_REVOKED");
    const oldLogin = await request("auth/login", { method: "POST", body: { username: user.username, password } }); await problem(oldLogin, 401, "INVALID_CREDENTIALS");
    const newLogin = await request("auth/login", { method: "POST", body: { username: user.username, password: replacement } }); assert.equal(newLogin.status, 200);
    const body = await newLogin.json(); assert.ok(!body.token && !body.user.password && !body.user.passwordHash);
  });
  await check("role changes and disabling an account atomically revoke its active session", async () => {
    const created = await write("POST", "users", { username: "revoke.test", displayName: "撤销验证", roles: ["receivingClerk"], enabled: true, password }, adminCookie);
    assert.equal(created.status, 201); let user = await created.json(); let cookie = await login(user.username);
    const body = () => ({ username: user.username, displayName: user.displayName, roles: user.roles, enabled: user.enabled, version: user.version });
    const changed = await write("PUT", "users/" + user.id, { ...body(), roles: ["shippingClerk"] }, adminCookie); assert.equal(changed.status, 200); user = await changed.json();
    await problem(await request("auth/me", { cookie }), 401, "SESSION_REVOKED");
    cookie = await login(user.username); assert.equal((await get("auth/me", cookie)).user.currentRole, "shippingClerk");
    const disabled = await write("PUT", "users/" + user.id, { ...body(), enabled: false }, adminCookie); assert.equal(disabled.status, 200);
    await problem(await request("auth/me", { cookie }), 401, "SESSION_REVOKED");
    await problem(await request("auth/login", { method: "POST", body: { username: user.username, password } }), 401, "INVALID_CREDENTIALS");
  });
  await check("last administrator and stale settings edits are rejected without changing state", async () => {
    const user = (await adminGet("users?search=admin")).items.find(x => x.username === "admin"); assert.ok(user);
    await problem(await write("PUT", "users/" + user.id, { username: user.username, displayName: user.displayName, roles: user.roles, enabled: false, version: user.version }, adminCookie), 409, "LAST_ADMINISTRATOR");
    assert.equal((await adminGet("users/" + user.id)).enabled, true);
    const settings = await adminGet("settings");
    const body = { defaultRackLevels: settings.defaultRackLevels, defaultCellsPerLevel: settings.defaultCellsPerLevel, lowThreshold: settings.lowThreshold, highThreshold: settings.highThreshold, sessionHours: settings.sessionHours, version: settings.version };
    const saved = await write("PUT", "settings", body, adminCookie); assert.equal(saved.status, 200);
    await problem(await write("PUT", "settings", body, adminCookie), 409, "VERSION_CONFLICT");
    assert.deepEqual(await adminGet("settings"), await saved.json());
  });
  await check("admin permissions follow the selected identity, never assigned-role union", async () => {
    const response = await write("POST", "users", { username: "dual.role", displayName: "双身份", roles: ["systemAdministrator", "receivingClerk"], enabled: true, password }, adminCookie); assert.equal(response.status, 201);
    const cookie = await login("dual.role");
    const switched = await request("auth/switch-role", { method: "POST", cookie, body: { role: "receivingClerk" } }); assert.equal(switched.status, 200);
    assert.equal((await get("auth/me", cookie)).user.currentRole, "receivingClerk");
    await problem(await request("users", { cookie }), 403, "FORBIDDEN");
    await problem(await write("POST", "bins", { zone: "禁止", rack: "R", level: "01", cell: "01" }, cookie), 403, "FORBIDDEN");
    const settings = await get("settings", cookie);
    await problem(await write("PUT", "settings", settings, cookie), 403, "FORBIDDEN");
  });
});
console.log(`PASS ${total} REAL B2b admin/bin/browser checks; stock/count/alert-state rules need backend integration tests`);
