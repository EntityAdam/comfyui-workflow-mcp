#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_directory="${1:-$(mktemp -d)}"
owns_package_directory=false
test_home="$(mktemp -d)"

if [[ $# -eq 0 ]]; then
  owns_package_directory=true
fi

cleanup() {
  rm -rf "$test_home"
  if [[ "$owns_package_directory" == true ]]; then
    rm -rf "$package_directory"
  fi
}
trap cleanup EXIT

if ! compgen -G "$package_directory/ComfyUi.WorkflowMcp.*.nupkg" >/dev/null; then
  dotnet pack "$repository_root/src/ComfyUi.WorkflowMcp/ComfyUi.WorkflowMcp.csproj" \
    --configuration Release \
    --output "$package_directory"
fi

version="$(
  dotnet msbuild "$repository_root/src/ComfyUi.WorkflowMcp/ComfyUi.WorkflowMcp.csproj" \
    -nologo \
    -getProperty:Version
)"
main_package="$package_directory/ComfyUi.WorkflowMcp.$version.nupkg"

test -f "$main_package"
unzip -l "$main_package" | grep '.mcp/server.json' >/dev/null
unzip -l "$main_package" | grep 'README.md' >/dev/null
unzip -l "$main_package" | grep 'DotnetToolSettings.xml' >/dev/null

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) runtime_identifier="osx-arm64" ;;
  Darwin-x86_64) runtime_identifier="osx-x64" ;;
  Linux-aarch64) runtime_identifier="linux-arm64" ;;
  Linux-x86_64) runtime_identifier="linux-x64" ;;
  *)
    echo "Unsupported package smoke-test platform: $(uname -s)-$(uname -m)" >&2
    exit 1
    ;;
esac

runtime_package="$package_directory/ComfyUi.WorkflowMcp.$runtime_identifier.$version.nupkg"
test -f "$runtime_package"
if unzip -l "$runtime_package" | grep -E ' (workflows|configuration|input)/' >/dev/null; then
  echo "Runtime package unexpectedly contains bundled workflows, configuration, or input media." >&2
  exit 1
fi

DOTNET_CLI_HOME="$test_home/dotnet" \
NUGET_PACKAGES="$test_home/packages" \
COMFY_MCP_PACKAGE_DIRECTORY="$package_directory" \
COMFY_MCP_PACKAGE_VERSION="$version" \
python3 - <<'PY'
import json
import os
import select
import signal
import socket
import subprocess
import sys
import time

# Stdio must remain usable even when its configured HTTP address is occupied.
occupied_listener = socket.socket()
occupied_listener.bind(("127.0.0.1", 0))
occupied_listener.listen()
occupied_url = f"http://127.0.0.1:{occupied_listener.getsockname()[1]}"
smoke_environment = dict(os.environ)
smoke_environment["ComfyWorkflowMcp__ServerUrl"] = occupied_url
smoke_environment["ASPNETCORE_URLS"] = occupied_url

command = [
    "dnx",
    f"ComfyUi.WorkflowMcp@{os.environ['COMFY_MCP_PACKAGE_VERSION']}",
    "--yes",
    "--no-http-cache",
    "--verbosity",
    "quiet",
    "--add-source",
    os.environ["COMFY_MCP_PACKAGE_DIRECTORY"],
    "--",
    "--stdio",
]
process = subprocess.Popen(
    command,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1,
    start_new_session=True,
    env=smoke_environment,
)
request = {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2025-06-18",
        "capabilities": {},
        "clientInfo": {"name": "package-smoke", "version": "1.0"},
    },
}
assert process.stdin is not None
assert process.stdout is not None
process.stdin.write(json.dumps(request) + "\n")
process.stdin.flush()

deadline = time.monotonic() + 60
stdout_diagnostics = []

def read_response(response_id):
    while time.monotonic() < deadline:
        readable, _, _ = select.select([process.stdout], [], [], 0.25)
        if readable:
            line = process.stdout.readline()
            if line:
                try:
                    candidate = json.loads(line)
                except json.JSONDecodeError:
                    stdout_diagnostics.append(line.rstrip())
                    continue
                if candidate.get("id") == response_id:
                    return candidate
        if process.poll() is not None:
            break
    return None

response = read_response(1)

if response is None or "result" not in response:
    stderr = process.stderr.read() if process.stderr is not None else ""
    if stdout_diagnostics:
        print("\n".join(stdout_diagnostics), file=sys.stderr)
    print(stderr, file=sys.stderr)
    raise SystemExit("Packaged MCP server did not answer initialize.")

if response["result"].get("protocolVersion") != "2025-06-18":
    raise SystemExit(f"Unexpected initialize response: {response}")

process.stdin.write(
    json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"
)
process.stdin.write(
    json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/call",
            "params": {"name": "workflow_list", "arguments": {}},
        }
    )
    + "\n"
)
process.stdin.flush()
workflow_response = read_response(2)

try:
    os.killpg(process.pid, signal.SIGTERM)
except ProcessLookupError:
    pass
try:
    process.wait(timeout=5)
except subprocess.TimeoutExpired:
    os.killpg(process.pid, signal.SIGKILL)
    process.wait()

occupied_listener.close()

result = workflow_response.get("result", {}) if workflow_response is not None else {}
structured = result.get("structuredContent")
if structured is None:
    try:
        structured = json.loads(result["content"][0]["text"])
    except (KeyError, IndexError, TypeError, json.JSONDecodeError):
        structured = None
if not isinstance(structured, dict) or structured.get("count") != 0:
    stderr = process.stderr.read() if process.stderr is not None else ""
    if stdout_diagnostics:
        print("\n".join(stdout_diagnostics), file=sys.stderr)
    print(stderr, file=sys.stderr)
    raise SystemExit(
        f"Packaged MCP server did not report an empty workflow catalog: {workflow_response}"
    )
PY
