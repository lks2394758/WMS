# WMS Claude Code MCP worker

This local MCP server lets Codex obtain a second opinion from **Claude Code** without driving the Claude Desktop window. It runs as a background stdio process and returns text only. Codex remains the only agent that edits this repository.

## Safety boundary

- `claude_status` checks the Claude Code executable only; it does not send project content anywhere.
- `claude_analyze` runs Claude Code in `plan` permission mode with permission prompts disabled, then returns its response as text.
- The worker exposes no write, shell, filesystem, or Git tools to its MCP client.
- Claude Code receives the requested analysis and may inspect this repository. Do not include secrets in a request.
- The worker is fixed to this repository root, derived from its own location. It does not accept an arbitrary workspace path.

The Claude Code permission mode and prompt are defense-in-depth controls, not a substitute for reviewing advice before acting on it.

## Prerequisites

1. Restart Codex Desktop after installing Claude Code, so its process receives the updated `PATH`.
2. In a normal terminal, run `claude` once and complete the Claude Code sign-in yourself.
3. Confirm `claude --version` works from a newly opened terminal.

The bridge deliberately does not automate browser sign-in or use Claude Desktop credentials.

## Run the protocol test

From the repository root:

```powershell
node tools/claude-worker-mcp/protocol-smoke-test.mjs
```

This test validates MCP initialization and tool discovery without invoking Claude Code.

## Register it in Codex

Register the bridge once from a terminal:

```powershell
codex mcp add wms-claude-worker -- node C:\Projects\WMS\wms-backend\tools\claude-worker-mcp\claude-worker-mcp.mjs
```

Restart Codex Desktop after registration. The MCP server will provide two tools:

- `claude_status` — verifies the local CLI is available.
- `claude_analyze` — asks Claude Code for constrained, read-only analysis of this repository.

If Claude Code is installed somewhere outside `PATH`, configure the MCP server environment variable `CLAUDE_CODE_PATH` with the full executable path. Keep that setting local to your machine; do not commit credentials or tokens.
