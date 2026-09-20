#!/usr/bin/env bash
# Launch Rhino 8 + Grasshopper and wait until Cassis MCP is listening.
# Cleans stale Rhino / port-3003 holders when MCP is unhealthy so relaunches succeed.
set -euo pipefail

MCP_URL="${CASSIS_MCP_URL:-http://127.0.0.1:3003/mcp/}"
TIMEOUT_SEC="${CASSIS_LAUNCH_TIMEOUT:-90}"
SKIP_CLEANUP="${CASSIS_SKIP_CLEANUP:-0}"

# Prefer 127.0.0.1 — localhost can hit IPv6 (::1) first and confuse probes.
if [[ "$MCP_URL" == *"localhost"* ]]; then
  MCP_URL="${MCP_URL//localhost/127.0.0.1}"
fi

# NOTE: A plain `curl GET http://…/mcp/` will hang — the transport opens a long-lived
# SSE stream (HTTP 200 + Keep-Alive). Always POST initialize and stop after the first event.
mcp_ready() {
  python3 - "$MCP_URL" <<'PY' 2>/dev/null
import json, sys, http.client, urllib.parse, threading
url = sys.argv[1]
u = urllib.parse.urlparse(url)
host = u.hostname or "127.0.0.1"
port = u.port or 80
path = u.path or "/mcp/"
if not path.endswith("/"):
    path += "/"
payload = json.dumps({
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "cassis-launch", "version": "1.0"},
    },
}).encode()
chunks = []
done = threading.Event()

def worker():
    try:
        conn = http.client.HTTPConnection(host, port, timeout=3)
        conn.request(
            "POST",
            path,
            body=payload,
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream",
            },
        )
        resp = conn.getresponse()
        while not done.is_set():
            chunk = resp.read(256)
            if not chunk:
                break
            chunks.append(chunk)
            text = b"".join(chunks)
            if b'"result"' in text or b'"serverInfo"' in text:
                break
            if len(text) > 65536:
                break
        try:
            conn.close()
        except Exception:
            pass
    except Exception:
        pass
    finally:
        done.set()

t = threading.Thread(target=worker, daemon=True)
t.start()
t.join(2.5)
done.set()
text = b"".join(chunks)
sys.exit(0 if (b'"result"' in text or b'"serverInfo"' in text) else 1)
PY
}

port_pids() {
  local pids=""
  if command -v lsof >/dev/null 2>&1; then
    pids=$(lsof -nP -iTCP:3003 -sTCP:LISTEN -t 2>/dev/null || true)
  fi
  echo "$pids"
}

rhino_pids() {
  if [[ "$(uname -s)" == "Darwin" ]]; then
    pgrep -f "/Contents/MacOS/Rhinoceros" 2>/dev/null || true
  else
    pgrep -f "[Rr]hino\.exe|[Rr]hinoceros" 2>/dev/null || true
  fi
}

wait_gone() {
  local pattern="$1"
  local seconds="${2:-20}"
  local i=0
  while (( i < seconds )); do
    if ! pgrep -f "$pattern" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
    i=$((i + 1))
  done
  return 1
}

cleanup_stale() {
  if [[ "$SKIP_CLEANUP" == "1" ]]; then
    echo "Skipping cleanup (CASSIS_SKIP_CLEANUP=1)"
    return 0
  fi

  if mcp_ready; then
    return 0
  fi

  local listeners rhino
  listeners=$(port_pids)
  rhino=$(rhino_pids)

  if [[ -z "$listeners" && -z "$rhino" ]]; then
    return 0
  fi

  echo "MCP unhealthy — clearing stale Rhino / port 3003…"

  # Quit Rhino first (releases HttpListener). Prefer graceful quit on macOS.
  if [[ -n "$rhino" ]]; then
    if [[ "$(uname -s)" == "Darwin" ]]; then
      osascript -e 'tell application "Rhinoceros" to quit' >/dev/null 2>&1 || true
      if ! wait_gone "/Contents/MacOS/Rhinoceros" 15; then
        echo "Force-killing Rhinoceros…"
        # shellcheck disable=SC2086
        kill $rhino 2>/dev/null || true
        sleep 1
        # shellcheck disable=SC2086
        kill -9 $rhino 2>/dev/null || true
        wait_gone "/Contents/MacOS/Rhinoceros" 10 || true
      fi
    else
      # shellcheck disable=SC2086
      kill $rhino 2>/dev/null || true
      sleep 2
      # shellcheck disable=SC2086
      kill -9 $rhino 2>/dev/null || true
    fi
  fi

  # Anything still bound to 3003 (orphan listener) — kill it.
  listeners=$(port_pids)
  if [[ -n "$listeners" ]]; then
    echo "Killing leftover listeners on :3003 ($listeners)"
    # shellcheck disable=SC2086
    kill $listeners 2>/dev/null || true
    sleep 1
    # shellcheck disable=SC2086
    kill -9 $listeners 2>/dev/null || true
  fi

  # Brief settle so the next bind succeeds.
  sleep 1
}

find_rhino_mac() {
  if [[ -n "${CASSIS_RHINO_PATH:-}" && -x "${CASSIS_RHINO_PATH}" ]]; then
    echo "$CASSIS_RHINO_PATH"
    return 0
  fi
  local c
  for c in \
    "/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros" \
    "/Volumes/Storage/00_Applications/Rhino 8.app/Contents/MacOS/Rhinoceros" \
    "$HOME/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros"; do
    if [[ -x "$c" ]]; then
      echo "$c"
      return 0
    fi
  done
  # Spotlight fallback (best-effort)
  if command -v mdfind >/dev/null 2>&1; then
    c=$(mdfind "kMDItemCFBundleIdentifier == 'com.mcneel.rhinoceros.8'" 2>/dev/null | head -1 || true)
    if [[ -n "$c" && -x "$c/Contents/MacOS/Rhinoceros" ]]; then
      echo "$c/Contents/MacOS/Rhinoceros"
      return 0
    fi
  fi
  return 1
}

find_rhino_win() {
  if [[ -n "${CASSIS_RHINO_PATH:-}" && -f "${CASSIS_RHINO_PATH}" ]]; then
    echo "$CASSIS_RHINO_PATH"
    return 0
  fi
  local c
  for c in \
    "/c/Program Files/Rhino 8/System/Rhino.exe" \
    "/mnt/c/Program Files/Rhino 8/System/Rhino.exe" \
    "/cygdrive/c/Program Files/Rhino 8/System/Rhino.exe"; do
    if [[ -f "$c" ]]; then
      echo "$c"
      return 0
    fi
  done
  return 1
}

launch_rhino() {
  local exe app
  if [[ "$(uname -s)" == "Darwin" ]]; then
    exe=$(find_rhino_mac) || {
      echo "Rhino 8 not found. Set CASSIS_RHINO_PATH to Rhinoceros." >&2
      exit 1
    }
    # Detach via LaunchServices. Launching Rhinoceros as a shell background job
    # makes it a child of Cursor/Terminal — when that session ends, Rhino dies.
    if [[ "$exe" == *"/Contents/MacOS/Rhinoceros" ]]; then
      app="${exe%/Contents/MacOS/Rhinoceros}"
    else
      app="$exe"
    fi
    echo "Starting Rhino + Grasshopper ($app)…"
    open -na "$app" --args -runscript="_Grasshopper"
    return
  fi

  exe=$(find_rhino_win) || {
    echo "Rhino 8 not found. Set CASSIS_RHINO_PATH." >&2
    exit 1
  }
  echo "Starting Rhino + Grasshopper ($exe)…"
  # Detach from this shell so the process survives script exit.
  if command -v nohup >/dev/null 2>&1; then
    nohup "$exe" /runscript="_Grasshopper" >/dev/null 2>&1 &
    disown $! 2>/dev/null || true
  else
    "$exe" /runscript="_Grasshopper" >/dev/null 2>&1 &
    disown $! 2>/dev/null || true
  fi
}

if mcp_ready; then
  echo "Cassis MCP already up at $MCP_URL"
  exit 0
fi

cleanup_stale

# After cleanup, MCP might still be up if quit failed oddly — recheck.
if mcp_ready; then
  echo "Cassis MCP already up at $MCP_URL"
  exit 0
fi

launch_rhino

echo "Waiting up to ${TIMEOUT_SEC}s for $MCP_URL …"
deadline=$((SECONDS + TIMEOUT_SEC))
while (( SECONDS < deadline )); do
  if mcp_ready; then
    echo "Cassis MCP ready at $MCP_URL"
    exit 0
  fi
  sleep 1
done

echo "Timed out waiting for Cassis MCP." >&2
echo "Is Auto-start off? Place the Cassis component, click Start Server," >&2
echo "or enable 'Auto-start MCP when Grasshopper loads' in the component menu." >&2
exit 1
