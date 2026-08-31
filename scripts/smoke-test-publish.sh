#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_directory="$(mktemp -d)"
server_log="$publish_directory/server.log"
server_pid=""

cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$publish_directory"
}
trap cleanup EXIT

dotnet publish "$repository_root/src/ComfyUi.WorkflowMcp/ComfyUi.WorkflowMcp.csproj" \
  --configuration Release \
  --no-restore \
  --output "$publish_directory"

test ! -e "$publish_directory/workflows"
test ! -e "$publish_directory/configuration"
test ! -e "$publish_directory/input"

port="$(python3 - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(("localhost", 0))
    print(listener.getsockname()[1])
PY
)"
server_url="http://localhost:$port"

(
  cd "$publish_directory"
  ComfyWorkflowMcp__BaseUrl="http://comfyui.invalid:8188" \
  ComfyWorkflowMcp__ServerUrl="$server_url" \
  dotnet ComfyUi.WorkflowMcp.dll >"$server_log" 2>&1
) &
server_pid="$!"

for _ in {1..50}; do
  if curl --silent --fail "$server_url/health" \
    | grep --quiet '"comfyui":"http://comfyui.invalid:8188"'; then
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    cat "$server_log"
    exit 1
  fi
  sleep 0.2
done

curl --silent --fail "$server_url/health" \
  | grep --quiet '"status":"ok"'

curl --silent --fail \
  --header "Accept: application/json, text/event-stream" \
  --header "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"publish-smoke","version":"1.0"}}}' \
  "$server_url/mcp" \
  | grep --quiet '"protocolVersion":"2025-06-18"'
