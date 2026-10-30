#!/usr/bin/env bash
# Builds and runs the sample game server on the local flavor (file store, no Couchbase) in one command.
#
# Builds samples/SampleHost (Debug), starts it with the launch profile "local" from samples/SampleHost, waits until
# GET /health/ready answers 200 (at most 60 s), prints the URLs and keeps streaming the host output. Ctrl+C stops the
# host gracefully (readiness 503, drain, commits complete); the script waits up to 35 s, then kills the process tree.
# No path leaves a host running after the script exits.
# Contract: specs/011-local-env/contracts/operations-cli.md section 4. Bash on macOS, Linux and Git Bash (Windows).
#
# Usage: tools/local/run-local.sh [--port <1-65535>] [--reset] [--lan]
#   --port   port to listen on (default 5080)
#   --reset  start with an empty store (the host deletes the configured store folder content after locking it)
#   --lan    listen on all interfaces so a phone on the same network can connect (development tools become reachable)
#
# Exit codes: 0 stopped by Ctrl+C after a graceful shutdown; 2 invalid argument; 3 .NET SDK of global.json not found;
# 4 port in use; 5 build failed; 6 host exited before ready (its own message is above); 7 readiness timeout;
# other = host exit code after it was ready.
set -u

readonly EXIT_INVALID_ARGUMENT=2
readonly EXIT_SDK_MISSING=3
readonly EXIT_PORT_IN_USE=4
readonly EXIT_BUILD_FAILED=5
readonly EXIT_HOST_EXITED=6
readonly EXIT_READY_TIMEOUT=7
readonly READY_TIMEOUT_SECONDS=60
readonly READY_POLL_SECONDS=0.5
readonly LOG_TAIL_LINES=20
readonly STOP_WAIT_SECONDS=35
readonly SDK_VERSION=10.0.403
readonly USAGE="Usage: tools/local/run-local.sh [--port <1-65535>] [--reset] [--lan]"

fail() {
    echo "run-local: $2" >&2
    exit "$1"
}

port=5080
reset=0
lan=0
while [ $# -gt 0 ]; do
    case "$1" in
        --port) [ $# -ge 2 ] || fail $EXIT_INVALID_ARGUMENT "--port needs a value. $USAGE"; port=$2; shift 2 ;;
        --port=*) port=${1#--port=}; shift ;;
        --reset) reset=1; shift ;;
        --lan) lan=1; shift ;;
        -h|--help) echo "$USAGE"; exit 0 ;;
        *) fail $EXIT_INVALID_ARGUMENT "unknown argument '$1'. $USAGE" ;;
    esac
done
case "$port" in
    ''|*[!0-9]*) fail $EXIT_INVALID_ARGUMENT "--port must be a number 1-65535, got '$port'. $USAGE" ;;
esac
if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
    fail $EXIT_INVALID_ARGUMENT "--port must be a number 1-65535, got '$port'. $USAGE"
fi

repo=$(cd "$(dirname "$0")/../.." && pwd)
host_dir="$repo/samples/SampleHost"

# 1. SDK: dotnet on PATH, else ~/.dotnet10 (repository convention). "dotnet --version" in the repository honours global.json.
dotnet=""
for candidate in "$(command -v dotnet 2>/dev/null)" "$HOME/.dotnet10/dotnet" "$HOME/.dotnet10/dotnet.exe"; do
    if [ -n "$candidate" ] && [ -x "$candidate" ] && (cd "$repo" && "$candidate" --version >/dev/null 2>&1); then
        dotnet=$candidate
        break
    fi
done
[ -n "$dotnet" ] || fail $EXIT_SDK_MISSING ".NET SDK $SDK_VERSION (global.json) not found. Looked at dotnet on PATH and ~/.dotnet10. Fix: install it from https://dotnet.microsoft.com/download/dotnet/10.0 or into ~/.dotnet10."

# IPv4 addresses of this machine's network interfaces (the --lan URLs and the --lan port probe).
lan_addresses() {
    if command -v ipconfig.exe >/dev/null 2>&1; then
        ipconfig.exe | tr -d '\r' | sed -n 's/.*IPv4[^:]*:[[:space:]]*\([0-9.]*\).*/\1/p'
    elif command -v hostname >/dev/null 2>&1 && hostname -I >/dev/null 2>&1; then
        hostname -I | tr ' ' '\n' | grep -E '^[0-9]+(\.[0-9]+){3}$'
    else
        ifconfig 2>/dev/null | sed -n 's/.*inet \([0-9.]*\).*/\1/p' | grep -v '^127\.'
    fi
}

# 2. Refuse a busy port before building: something accepting on a loopback address (the host binds localhost = both). With
# --lan the host binds all interfaces, so a server on 0.0.0.0 or on one of the LAN addresses blocks it too. Connect probes
# (bash cannot bind): connecting to 0.0.0.0 reaches this machine on Linux/macOS and fails at once on Windows.
probe_addresses="127.0.0.1 ::1"
[ "$lan" -eq 1 ] && probe_addresses="$probe_addresses 0.0.0.0 $(lan_addresses | tr '\n' ' ')"
for address in $probe_addresses; do
    if (exec 3<>"/dev/tcp/$address/$port") 2>/dev/null; then
        fail $EXIT_PORT_IN_USE "port $port is in use ($address). Fix: stop the other server or pick another port with --port <n>."
    fi
done

# 3. Build (output visible).
"$dotnet" build "$host_dir/SampleHost.csproj" -c Debug || fail $EXIT_BUILD_FAILED "build of samples/SampleHost failed (see the compiler output above)."

listen_host=localhost
[ "$lan" -eq 1 ] && listen_host=0.0.0.0
run_args=(run --no-build --launch-profile local -- --urls "http://$listen_host:$port")
[ "$reset" -eq 1 ] && run_args+=(--FileStore:ResetOnStart=true)
base_url="http://localhost:$port/"

state=$(mktemp -d "${TMPDIR:-/tmp}/run-local.XXXXXX")
poller=""
cleanup() {
    if [ -n "$poller" ]; then kill "$poller" 2>/dev/null; wait "$poller" 2>/dev/null; fi
    rm -rf "$state"
}
trap cleanup EXIT

# GET /health/ready over /dev/tcp (no curl needed); true on HTTP 200.
ready_now() {
    local status
    status=$( (exec 3<>"/dev/tcp/127.0.0.1/$port" || exit 1
        printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
        IFS= read -r line <&3 && printf '%s' "$line") 2>/dev/null)
    case "$status" in "HTTP/"*" 200"*) return 0 ;; *) return 1 ;; esac
}

# Kills the host (dotnet run and the app it started). Git Bash: native process tree; elsewhere: children first.
kill_tree() {
    local pid=$1 child
    if [ -r "/proc/$pid/winpid" ]; then
        taskkill //PID "$(cat "/proc/$pid/winpid")" //T //F >/dev/null 2>&1
        return
    fi
    for child in $(pgrep -P "$pid" 2>/dev/null); do kill_tree "$child"; done
    kill -KILL "$pid" 2>/dev/null
}

show_log_tail() {
    local newest
    newest=$(ls -t "$host_dir/logs"/* 2>/dev/null | head -n 1)
    [ -n "$newest" ] || return 0
    echo "Last $LOG_TAIL_LINES lines of $newest:"
    tail -n "$LOG_TAIL_LINES" "$newest" | sed 's/^/  /'
}

print_urls() {
    local folder note=""
    folder=$(sed -n 's/.*"Folder"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$host_dir/appsettings.Local.json" | head -n 1)
    [ "$reset" -eq 1 ] && note=", started empty"
    echo
    printf '\033[32mLocal game server is ready (flavor Local, data %s%s)\033[0m\n' "${folder:-see appsettings.Local.json}" "$note"
    echo "  Player API   ${base_url}          (routes /api/v1/..., /v1/time)"
    echo "  Health       ${base_url}health/ready"
    echo "  GM UI        ${base_url}gm/v1/ui  (staff admin / dev-only-change-me)"
    echo "  GM OpenAPI   ${base_url}gm/v1/openapi/gm.json"
    if [ "$lan" -eq 1 ]; then
        printf '\n\033[31mWARNING: the server listens on all network interfaces (--lan).\n'
        printf '  The GM UI with the development staff seed, test authentication and cheats are reachable from the network.\n'
        printf '  Use --lan only on a trusted network, and stop the server when the device test is done.\n'
        for address in $(lan_addresses); do printf '  From a device: http://%s:%s/\n' "$address" "$port"; done
        printf '\033[0m'
    fi
    echo "Press Ctrl+C to stop."
    echo
}

# 4./5./6./7. Background watcher: readiness, then the URL block; on timeout it stops the host. The host runs in the foreground
# so the terminal's SIGINT reaches it (background jobs of a non-interactive shell ignore SIGINT). The script cannot act while
# it waits for that foreground host (bash runs traps only after the command ends), so the watcher stays until the host exits
# and handles Ctrl+C: it traps SIGINT (an async subshell may replace the inherited "ignore" since bash 4.4, POSIX
# interpretation 751) and kills the process tree when the host has not stopped STOP_WAIT_SECONDS later.
(
    trap - EXIT
    stop_deadline=""
    trap '[ -n "$stop_deadline" ] || stop_deadline=$((SECONDS + STOP_WAIT_SECONDS))' INT
    while [ ! -s "$state/pid" ]; do sleep 0.1; done
    host_pid=$(cat "$state/pid")
    start=$SECONDS
    ready=0
    while kill -0 "$host_pid" 2>/dev/null; do
        if [ -n "$stop_deadline" ]; then
            if [ "$SECONDS" -ge "$stop_deadline" ]; then
                echo "run-local: the host did not stop within $STOP_WAIT_SECONDS s; killing the process tree." >&2
                kill_tree "$host_pid"
                exit 0
            fi
        elif [ "$ready" -eq 0 ]; then
            if ready_now; then
                ready=1
                touch "$state/ready"
                print_urls
            elif [ $((SECONDS - start)) -ge "$READY_TIMEOUT_SECONDS" ]; then
                touch "$state/timeout"
                show_log_tail
                kill_tree "$host_pid"
                exit 0
            fi
        fi
        sleep "$READY_POLL_SECONDS"
    done
) &
poller=$!

# "exec" keeps the PID written to the state folder: it becomes the dotnet process the poller may have to stop.
(cd "$host_dir" && sh -c 'echo $$ > "$0"; exec "$@"' "$state/pid" "$dotnet" "${run_args[@]}")
code=$?
# The host is gone: the watcher (still waiting for readiness after a start failure, or for the stop) has nothing left to do.
kill "$poller" 2>/dev/null
wait "$poller" 2>/dev/null
poller=""

if [ -e "$state/timeout" ]; then
    fail $EXIT_READY_TIMEOUT "the host was not ready after $READY_TIMEOUT_SECONDS s (GET http://127.0.0.1:$port/health/ready)."
fi
if [ ! -e "$state/ready" ]; then
    fail $EXIT_HOST_EXITED "the host exited with code $code before it was ready; its own message is above (data folder locked by another host, malformed file, invalid configuration)."
fi
exit "$code"
