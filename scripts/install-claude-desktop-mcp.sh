#!/usr/bin/env bash
#
# install-claude-desktop-mcp.sh
#
# Publishes the ORUK MCP server and registers it in the Claude Desktop config so
# the `oruk` tools (search_services, search_organisations, list_taxonomy_terms, …)
# are available in Claude Desktop.
#
# Why publish instead of `dotnet run`:
#   The MCP stdio transport owns stdout for JSON-RPC. `dotnet run` writes build
#   output to stdout, which corrupts the protocol — so we publish once and point
#   Claude Desktop at the built DLL via `dotnet <dll>` instead.
#
# The command uses an ABSOLUTE path to `dotnet` because Claude Desktop launches
# MCP servers with a minimal environment that usually does not include it on PATH.
#
# Safe to re-run: it re-publishes and overwrites only the `oruk` server entry,
# preserving every other key in the config. The previous config is backed up.
#
# Usage:
#   scripts/install-claude-desktop-mcp.sh [--server-name NAME] [--config PATH] [--print]
#
#   --server-name NAME   Key to use under mcpServers (default: oruk)
#   --config PATH        Claude Desktop config file to update
#                        (default: macOS Claude Desktop location)
#   --print              Don't write anything; print the merged config to stdout

set -euo pipefail

SERVER_NAME="oruk"
CONFIG_PATH="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
PRINT_ONLY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-name) SERVER_NAME="$2"; shift 2 ;;
    --config)      CONFIG_PATH="$2"; shift 2 ;;
    --print)       PRINT_ONLY=1; shift ;;
    -h|--help)     grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

# ── Resolve paths ───────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/OrukTransformer.Mcp/OrukTransformer.Mcp.csproj"

if [[ ! -f "$PROJECT" ]]; then
  echo "error: cannot find MCP project at $PROJECT" >&2
  exit 1
fi

DOTNET_BIN="$(command -v dotnet || true)"
if [[ -z "$DOTNET_BIN" ]]; then
  echo "error: 'dotnet' not found on PATH. Install the .NET SDK first." >&2
  exit 1
fi
# Resolve any symlink to a stable absolute path for the config entry.
DOTNET_BIN="$(cd "$(dirname "$DOTNET_BIN")" && pwd)/$(basename "$DOTNET_BIN")"

# ── Publish the server ──────────────────────────────────────────────────────────
PUBLISH_DIR="$REPO_ROOT/src/OrukTransformer.Mcp/bin/Release/net10.0/publish"
echo "Publishing ORUK MCP server (Release) → $PUBLISH_DIR"
"$DOTNET_BIN" publish "$PROJECT" -c Release -o "$PUBLISH_DIR" --nologo -v quiet

DLL="$PUBLISH_DIR/OrukTransformer.Mcp.dll"
if [[ ! -f "$DLL" ]]; then
  echo "error: expected published DLL not found at $DLL" >&2
  exit 1
fi
if [[ ! -f "$PUBLISH_DIR/feeds.json" ]]; then
  echo "warning: feeds.json is not next to the DLL — the server will start with no feeds." >&2
fi

# ── Merge into the Claude Desktop config ────────────────────────────────────────
# python3 is used for a safe JSON read-modify-write that preserves all other keys.
PYTHON_BIN="$(command -v python3 || true)"
if [[ -z "$PYTHON_BIN" ]]; then
  echo "error: python3 is required to edit the JSON config safely." >&2
  exit 1
fi

SERVER_NAME="$SERVER_NAME" CONFIG_PATH="$CONFIG_PATH" DOTNET_BIN="$DOTNET_BIN" \
DLL="$DLL" PRINT_ONLY="$PRINT_ONLY" "$PYTHON_BIN" - <<'PY'
import json, os, sys, shutil, datetime

cfg_path   = os.environ["CONFIG_PATH"]
name       = os.environ["SERVER_NAME"]
dotnet_bin = os.environ["DOTNET_BIN"]
dll        = os.environ["DLL"]
print_only = os.environ["PRINT_ONLY"] == "1"

# Load existing config (preserve every existing key), or start fresh.
config = {}
if os.path.exists(cfg_path) and os.path.getsize(cfg_path) > 0:
    try:
        with open(cfg_path, "r", encoding="utf-8") as fh:
            config = json.load(fh)
    except json.JSONDecodeError as exc:
        print(f"error: existing config at {cfg_path} is not valid JSON: {exc}", file=sys.stderr)
        sys.exit(1)

servers = config.setdefault("mcpServers", {})
existed = name in servers
servers[name] = {
    "command": dotnet_bin,
    "args": [dll],
}

merged = json.dumps(config, indent=2) + "\n"

if print_only:
    sys.stdout.write(merged)
    sys.exit(0)

# Back up, then write atomically.
os.makedirs(os.path.dirname(cfg_path), exist_ok=True)
if os.path.exists(cfg_path):
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = f"{cfg_path}.bak-{stamp}"
    shutil.copy2(cfg_path, backup)
    print(f"Backed up existing config → {backup}")

tmp = cfg_path + ".tmp"
with open(tmp, "w", encoding="utf-8") as fh:
    fh.write(merged)
os.replace(tmp, cfg_path)

action = "Updated" if existed else "Added"
print(f"{action} mcpServers['{name}'] in {cfg_path}")
print(f"  command: {dotnet_bin}")
print(f"  args:    [{dll}]")
PY

if [[ "$PRINT_ONLY" -eq 0 ]]; then
  echo
  echo "Done. Restart Claude Desktop (quit fully and reopen) to load the '$SERVER_NAME' server."
fi
