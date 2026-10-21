// Shared real-v2 test infrastructure. No installed packages; only temporary databases and existing Edge/Chrome.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { existsSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createAppServer, loadConfig } from "../../server.mjs";

const repo = fileURLToPath(new URL("../../../../", import.meta.url));
export const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
export async function freePort() {
  const probe = createServer().listen(0, "127.0.0.1"); await once(probe, "listening");
  const port = probe.address().port; await new Promise(resolve => probe.close(resolve)); return port;
}
export async function withRealV2(action) {
  const apiDir = join(repo, "src", "Wms.Api"), dll = join(apiDir, "bin", "Debug", "net10.0", "Wms.Api.dll");
  if (!existsSync(dll)) throw new Error("Build src/Wms.Api first.");
  const work = mkdtempSync(join(tmpdir(), "wms-v2-real-ui-")), password = randomBytes(24).toString("base64url");
  const legacy = join(work, "wms-demo.db"), sentinel = randomBytes(4096); writeFileSync(legacy, sentinel);
  let child, ui; const output = [], credentials = [];
  try {
    const upstream = `http://127.0.0.1:${await freePort()}`;
    child = spawn("dotnet", [dll], { cwd: apiDir, windowsHide: true, stdio: ["ignore", "pipe", "pipe"], env: {
      ...process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: upstream,
      V2__Enabled: "true", V2__Sqlite__DataSource: join(work, "wms-v2-demo.db"), V2__Seed__Enabled: "true",
      V2__Seed__Password: password, Persistence__Sqlite__DataSource: legacy, Logging__LogLevel__Default: "Warning"
    } });
    child.stdout.on("data", data => output.push(data.toString())); child.stderr.on("data", data => output.push(data.toString()));
    let launchError; child.on("error", error => { launchError = error; });
    let ready = false;
    for (let i = 0; i < 120; i++) {
      if (launchError || child.exitCode !== null || child.signalCode !== null) throw new Error("The built v2 API did not start; run the backend tests for diagnostics.");
      const response = await fetch(upstream + "/health/ready", { signal: AbortSignal.timeout(1000) }).catch(() => null);
      if (response?.status === 200) { ready = true; break; } await pause(250);
    }
    if (!ready) throw new Error("The built v2 API was not ready in 30 seconds.");
    ui = createAppServer(loadConfig({ WMS_V2_API_BASE_URL: upstream }), { logger: { log: line => output.push(line) } });
    ui.listen(0, "127.0.0.1"); await once(ui, "listening"); const base = `http://127.0.0.1:${ui.address().port}`;
    const request = (path, { method = "GET", body, cookie, key } = {}) => fetch(base + "/api/v2/" + path, {
      method, signal: AbortSignal.timeout(22000), headers: { Accept: "application/json", ...(cookie ? { Cookie: cookie } : {}),
        ...(key ? { "Idempotency-Key": key } : {}), ...(body === undefined ? {} : { "Content-Type": "application/json", Origin: base }) },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const login = async username => {
      const response = await request("auth/login", { method: "POST", body: { username, password } }); assert.equal(response.status, 200);
      const body = await response.json(), cookie = response.headers.get("set-cookie")?.split(";", 1)[0];
      assert.ok(cookie && !body.token); credentials.push(cookie.split("=")[1]); return cookie;
    };
    await action({ repo, work, base, upstream, password, request, login });
    assert.ok(readFileSync(legacy).equals(sentinel)); assert.ok(!existsSync(legacy + "-wal") && !existsSync(legacy + "-shm"));
    assert.ok(!output.join("\n").includes(password)); for (const value of credentials) assert.ok(!output.join("\n").includes(value));
  } finally {
    ui?.closeAllConnections(); if (ui) await new Promise(resolve => ui.close(resolve));
    if (child?.pid && child.exitCode === null && child.signalCode === null) { const ended = once(child, "exit"); child.kill(); await ended; }
    const within = relative(resolve(tmpdir()), resolve(work));
    if (within && !within.startsWith("..") && !within.includes(":") && within.startsWith("wms-v2-real-ui-")) {
      try { rmSync(work, { recursive: true, force: true }); } catch { /* A stopped process may still be releasing its temporary handles. */ }
    }
  }
}

export async function withBrowser(work, action) {
  const executable = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Google/Chrome/Application/chrome.exe"].find(existsSync);
  if (!executable) throw new Error("No existing Edge/Chrome; do not install a browser automatically.");
  const port = await freePort(), debugBase = `http://127.0.0.1:${port}`;
  const browser = spawn(executable, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${join(work, "browser-profile")}`, "--no-first-run", "--no-default-browser-check", "--disable-extensions", "about:blank"], { stdio: "ignore", windowsHide: true });
  let socket, sequence = 0, launchError, cdp; const pending = new Map(), errors = [];
  browser.on("error", error => { launchError = error; });
  try {
    for (let i = 0; i < 100; i++) {
      // On Windows the launcher may exit successfully after handing off to the actual browser process.
      if (launchError || (browser.exitCode !== null && browser.exitCode !== 0) || browser.signalCode !== null) throw new Error("The existing browser could not be launched.");
      const targets = await fetch(debugBase + "/json/list", { signal: AbortSignal.timeout(1000) }).then(r => r.json()).catch(() => []);
      const page = targets.find(x => x.type === "page");
      if (page) { socket = new WebSocket(page.webSocketDebuggerUrl); await once(socket, "open"); break; } await pause(100);
    }
    if (!socket) throw new Error("The existing browser did not start.");
    socket.onmessage = event => {
      const message = JSON.parse(event.data);
      if (message.id) { const item = pending.get(message.id); if (item) { pending.delete(message.id); clearTimeout(item.timeout); message.error ? item.reject(new Error(message.error.message)) : item.resolve(message.result); } }
      else if (message.method === "Runtime.exceptionThrown") errors.push(message.params.exceptionDetails.text);
    };
    socket.onclose = () => { for (const item of pending.values()) { clearTimeout(item.timeout); item.reject(new Error("The test browser connection closed.")); } pending.clear(); };
    cdp = (method, params = {}) => new Promise((resolve, reject) => {
      const id = ++sequence, timeout = setTimeout(() => { pending.delete(id); reject(new Error("Test browser command timed out: " + method)); }, 10000);
      pending.set(id, { resolve, reject, timeout }); socket.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async expression => {
      const value = await cdp("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
      if (value.exceptionDetails) throw new Error("The test page threw an exception."); return value.result.value;
    };
    const wait = async (expression, label) => { for (let i = 0; i < 150; i++) { if (await evaluate(expression)) return; await pause(100); } throw new Error("Timed out: " + label); };
    const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const field = (selector, value) => evaluate(`(()=>{const el=document.querySelector(${JSON.stringify(selector)});el.value=${JSON.stringify(value)};el.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await cdp("Page.enable"); await cdp("Runtime.enable");
    await action({ cdp, evaluate, wait, click, field }); assert.deepEqual(errors, []);
  } finally {
    // Close this isolated browser/profile through CDP even if its Windows launcher already exited.
    if (cdp && socket?.readyState === WebSocket.OPEN) { try { await Promise.race([cdp("Browser.close"), pause(1000)]); } catch { /* Browser closure may close the protocol before its reply. */ } }
    socket?.close();
    if (browser.pid && browser.exitCode === null && browser.signalCode === null) { const ended = once(browser, "exit"); browser.kill(); await Promise.race([ended, pause(3000)]); }
  }
}
