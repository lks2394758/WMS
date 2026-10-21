import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const bridgePath = path.join(testDirectory, "claude-worker-mcp.mjs");
const bridge = spawn(process.execPath, [bridgePath], {
  stdio: ["pipe", "pipe", "pipe"],
  windowsHide: true,
});

let buffer = "";
const replies = [];
let stderr = "";

bridge.stdout.on("data", (chunk) => {
  buffer += chunk.toString();
  const lines = buffer.split("\n");
  buffer = lines.pop();
  for (const line of lines) {
    if (line.trim()) {
      replies.push(JSON.parse(line));
    }
  }
});
bridge.stderr.on("data", (chunk) => {
  stderr += chunk.toString();
});

bridge.stdin.write(`${JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  method: "initialize",
  params: { protocolVersion: "2025-03-26" },
})}\n`);
bridge.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" })}\n`);
bridge.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" })}\n`);

const deadline = Date.now() + 5_000;
while (replies.length < 2 && Date.now() < deadline) {
  await new Promise((resolve) => setTimeout(resolve, 20));
}

bridge.kill();

assert.equal(replies.length, 2, `Expected two MCP responses. stderr: ${stderr}`);
const initialization = replies.find((reply) => reply.id === 1);
const toolList = replies.find((reply) => reply.id === 2);

assert.equal(initialization?.result?.serverInfo?.name, "wms-claude-worker");
assert.equal(initialization?.result?.protocolVersion, "2025-03-26");
assert.deepEqual(
  toolList?.result?.tools?.map((tool) => tool.name),
  ["claude_status", "claude_analyze"],
);

console.log("MCP protocol smoke test passed.");
