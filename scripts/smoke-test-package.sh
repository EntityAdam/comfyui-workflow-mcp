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

if ! compgen -G "$package_directory/ComfyUi.Mcp.*.nupkg" >/dev/null; then
  dotnet pack "$repository_root/src/ComfyUi.Mcp/ComfyUi.Mcp.csproj" \
    --configuration Release \
    --output "$package_directory"
fi

version="$(
  dotnet msbuild "$repository_root/src/ComfyUi.Mcp/ComfyUi.Mcp.csproj" \
    -nologo \
    -getProperty:Version
)"
main_package="$package_directory/ComfyUi.Mcp.$version.nupkg"

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

runtime_package="$package_directory/ComfyUi.Mcp.$runtime_identifier.$version.nupkg"
test -f "$runtime_package"
unzip -l "$runtime_package" | grep 'workflows/image_flux2.json' >/dev/null
unzip -l "$runtime_package" | grep 'configuration/workflows/image_flux2.yaml' >/dev/null

DOTNET_CLI_HOME="$test_home/dotnet" \
NUGET_PACKAGES="$test_home/packages" \
COMFY_MCP_PACKAGE_DIRECTORY="$package_directory" \
COMFY_MCP_PACKAGE_VERSION="$version" \
python3 - <<'PY'
import json
import os
import select
import subprocess
import sys
import time

command = [
    "dnx",
    f"ComfyUi.Mcp@{os.environ['COMFY_MCP_PACKAGE_VERSION']}",
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

process.terminate()
try:
    process.wait(timeout=5)
except subprocess.TimeoutExpired:
    process.kill()
    process.wait()

if workflow_response is None or "image_flux2" not in json.dumps(workflow_response):
    stderr = process.stderr.read() if process.stderr is not None else ""
    if stdout_diagnostics:
        print("\n".join(stdout_diagnostics), file=sys.stderr)
    print(stderr, file=sys.stderr)
    raise SystemExit(
        f"Packaged MCP server could not read bundled workflows: {workflow_response}"
    )
PY
