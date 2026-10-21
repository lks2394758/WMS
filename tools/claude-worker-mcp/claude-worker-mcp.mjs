#!/usr/bin/env node

/**
 * A deliberately small MCP stdio server that delegates read-only analysis to
 * Claude Code.  It has no file-writing tool: Codex remains the sole editor.
 */
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import path from "node:path";

const MAX_PROMPT_CHARS = 16_000;
const MAX_OUTPUT_BYTES = 1_000_000;
const ANALYSIS_TIMEOUT_MS = 300_000;
const STATUS_TIMEOUT_MS = 15_000;
const MAX_TURNS = 5;

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const workspaceRoot = path.resolve(scriptDirectory, "..", "..");
const claudeCommand = process.env.CLAUDE_CODE_PATH || "claude";

const toolDefinitions = [
  {
    name: "claude_status",
    description:
      "Checks whether the local Claude Code command is available. This does not send project content to Claude.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false,
    },
  },
  {
    name: "claude_analyze",
    description:
      "Ask local Claude Code for a read-only analysis of this WMS repository. It returns text only and never exposes a file-writing operation.",
    inputSchema: {
      type: "object",
      properties: {
        prompt: {
          type: "string",
          description: "The focused analysis question for Claude Code.",
          minLength: 1,
          maxLength: MAX_PROMPT_CHARS,
        },
        max_turns: {
          type: "integer",
          description: "Maximum Claude Code turns to use (1-5; default 2).",
          minimum: 1,
          maximum: MAX_TURNS,
        },
      },
      required: ["prompt"],
      additionalProperties: false,
    },
  },
];

function writeMessage(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function log(message) {
  process.stderr.write(`[wms-claude-worker] ${message}\n`);
}

function respond(id, result) {
  writeMessage({ jsonrpc: "2.0", id, result });
}

function respondError(id, code, message) {
  writeMessage({ jsonrpc: "2.0", id, error: { code, message } });
}

function toolError(message) {
  return {
    content: [{ type: "text", text: message }],
    isError: true,
  };
}

function truncate(text, maximum = 8_000) {
  return text.length <= maximum ? text : `${text.slice(0, maximum)}\n…[truncated]`;
}

function runClaude(argumentsList, timeoutMs) {
  return new Promise((resolve, reject) => {
    let stdout = "";
    let stderr = "";
    let outputExceeded = false;
    let timedOut = false;

    const child = spawn(claudeCommand, argumentsList, {
      cwd: workspaceRoot,
      shell: false,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });

    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, timeoutMs);

    const collect = (target, chunk) => {
      if (outputExceeded) {
        return target;
      }

      const next = target + chunk.toString();
      if (Buffer.byteLength(next, "utf8") > MAX_OUTPUT_BYTES) {
        outputExceeded = true;
        child.kill();
        return next.slice(0, MAX_OUTPUT_BYTES);
      }

      return next;
    };

    child.stdout.on("data", (chunk) => {
      stdout = collect(stdout, chunk);
    });
    child.stderr.on("data", (chunk) => {
      stderr = collect(stderr, chunk);
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (exitCode, signal) => {
      clearTimeout(timer);

      if (timedOut) {
        reject(new Error(`Claude Code exceeded the ${Math.round(timeoutMs / 1000)} second limit.`));
        return;
      }
      if (outputExceeded) {
        reject(new Error("Claude Code produced more than 1 MB of output."));
        return;
      }
      if (exitCode !== 0) {
        const detail = truncate(stderr.trim() || stdout.trim() || `exit code ${exitCode}, signal ${signal ?? "none"}`);
        reject(new Error(`Claude Code did not complete successfully: ${detail}`));
        return;
      }

      resolve(stdout.trim());
    });
  });
}

function analysisPrompt(userPrompt) {
  return [
    "You are a read-only architecture and code-review advisor.",
    "Work only inside the current repository. Do not edit, create, delete, rename, stage, or commit files.",
    "Do not run commands that can modify the system, repository, dependencies, or network state.",
    "Inspect only the material needed to answer the question. Return concise Markdown with findings, uncertainty, and suggested verification steps.",
    "You are not the implementation owner; report advice only.",
    "",
    "Question:",
    userPrompt,
  ].join("\n");
}

function extractResult(stdout) {
  try {
    const parsed = JSON.parse(stdout);
    if (typeof parsed.result === "string") {
      return parsed.result;
    }
    return JSON.stringify(parsed, null, 2);
  } catch {
    return stdout || "Claude Code completed without returning text.";
  }
}

async function callTool(name, toolArguments = {}) {
  if (name === "claude_status") {
    const version = await runClaude(["--version"], STATUS_TIMEOUT_MS);
    return {
      content: [
        {
          type: "text",
          text: `Claude Code is available for the WMS analysis bridge.\nCommand: ${claudeCommand}\nVersion: ${version}`,
        },
      ],
    };
  }

  if (name === "claude_analyze") {
    const prompt = toolArguments.prompt;
    if (typeof prompt !== "string" || prompt.trim().length === 0) {
      return toolError("The 'prompt' argument must be a non-empty string.");
    }
    if (prompt.length > MAX_PROMPT_CHARS) {
      return toolError(`The prompt must be at most ${MAX_PROMPT_CHARS} characters.`);
    }

    const requestedTurns = toolArguments.max_turns ?? 2;
    if (!Number.isInteger(requestedTurns) || requestedTurns < 1 || requestedTurns > MAX_TURNS) {
      return toolError(`The 'max_turns' argument must be an integer from 1 through ${MAX_TURNS}.`);
    }

    const stdout = await runClaude(
      [
        "-p",
        "--output-format",
        "json",
        "--permission-mode",
        "plan",
        "--permission-prompts",
        "none",
        "--max-turns",
        String(requestedTurns),
        analysisPrompt(prompt),
      ],
      ANALYSIS_TIMEOUT_MS,
    );

    return {
      content: [{ type: "text", text: extractResult(stdout) }],
    };
  }

  return toolError(`Unknown tool: ${name}`);
}

async function handleRequest(message) {
  const hasId = Object.hasOwn(message, "id");

  if (message.method === "initialize") {
    if (hasId) {
      respond(message.id, {
        protocolVersion: message.params?.protocolVersion ?? "2025-03-26",
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: "wms-claude-worker", version: "1.0.0" },
        instructions:
          "This server runs Claude Code as a read-only WMS analysis advisor. It returns text only; Codex remains responsible for every workspace change.",
      });
    }
    return;
  }

  if (message.method === "notifications/initialized" || message.method === "notifications/cancelled") {
    return;
  }

  if (message.method === "ping") {
    if (hasId) {
      respond(message.id, {});
    }
    return;
  }

  if (message.method === "tools/list") {
    if (hasId) {
      respond(message.id, { tools: toolDefinitions });
    }
    return;
  }

  if (message.method === "tools/call") {
    if (!hasId) {
      return;
    }

    try {
      respond(message.id, await callTool(message.params?.name, message.params?.arguments));
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      respond(message.id, toolError(detail));
    }
    return;
  }

  if (hasId) {
    respondError(message.id, -32601, `Method not found: ${message.method ?? "unknown"}`);
  }
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  if (!line.trim()) {
    return;
  }

  let message;
  try {
    message = JSON.parse(line);
  } catch {
    log("Ignoring an invalid JSON-RPC message.");
    return;
  }

  void handleRequest(message).catch((error) => {
    log(`Unhandled request error: ${error instanceof Error ? error.message : String(error)}`);
  });
});
