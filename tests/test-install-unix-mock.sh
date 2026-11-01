#!/usr/bin/env bash
set -euo pipefail

SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_DIR="$(mktemp -d "${TMPDIR:-/tmp}/session-memory-mock.XXXXXXXX")"
trap 'rm -rf "$TEST_DIR"' EXIT
export HOME="$TEST_DIR/home" XDG_DATA_HOME="$TEST_DIR/data" MOCK_LOG="$TEST_DIR/events"
export REAL_AWK="$(command -v awk)"
export REAL_MV="$(command -v mv)"
export PATH="$TEST_DIR/tools:$PATH"
mkdir -p "$HOME/.leann/models/facebook-contriever" "$TEST_DIR/tools"
touch "$HOME/.leann/models/facebook-contriever/model.onnx"

cat > "$TEST_DIR/tools/uname" <<'EOF'
#!/bin/sh
echo "$MOCK_OS"
EOF
cat > "$TEST_DIR/tools/awk" <<'EOF'
#!/bin/sh
case "$*" in
    */proc/meminfo) echo 8388608 ;;
    *) exec "$REAL_AWK" "$@" ;;
esac
EOF
cat > "$TEST_DIR/tools/mv" <<'EOF'
#!/bin/sh
if [ "${MOCK_FAIL_SWAP:-0}" = 1 ] && [ ! -e "$MOCK_LOG.swap-failed" ] &&
   { [ "$1" = "$XDG_DATA_HOME/LeannSessionMemory/staging" ] || [ "$1" = "$HOME/Library/Application Support/LeannSessionMemory/staging" ]; }; then
    : > "$MOCK_LOG.swap-failed"
    exit 1
fi
exec "$REAL_MV" "$@"
EOF
cat > "$TEST_DIR/tools/dotnet" <<'EOF'
#!/bin/sh
case "$1" in
    --list-sdks) echo '10.0.100 [mock]' ;;
    --list-runtimes) : ;;
    publish)
        while [ "$1" != --output ]; do shift; done
        mkdir -p "$2"
        touch "$2/SessionMemory.dll"
        ;;
    tool)
        while [ "$1" != --tool-path ]; do shift; done
        mkdir -p "$2"
        printf '#!/bin/sh\nexit 0\n' > "$2/leann-dotnet"
        chmod +x "$2/leann-dotnet"
        ;;
    */SessionMemory.dll)
        shift
        [ "$*" = 'run --once --progress' ] || exit 99
        echo index >> "$MOCK_LOG"
        [ "${MOCK_FAIL_INDEX:-0}" != 1 ] || exit 7
        ;;
    *) exit 99 ;;
esac
EOF
cat > "$TEST_DIR/tools/launchctl" <<'EOF'
#!/bin/sh
case "$1" in
    print) [ "${MOCK_RUNNING:-0}" = 1 ] && echo 'state = running'; exit 0 ;;
    bootstrap|load) echo start >> "$MOCK_LOG" ;;
esac
EOF
cat > "$TEST_DIR/tools/systemctl" <<'EOF'
#!/bin/sh
case "$*" in
    *is-active*|*is-enabled*) [ "${MOCK_RUNNING:-0}" = 1 ]; exit $? ;;
    *" start "*) echo start >> "$MOCK_LOG" ;;
    *restart*) echo start >> "$MOCK_LOG" ;;
esac
EOF
for command in pkill sleep loginctl; do
    printf '#!/bin/sh\nexit 0\n' > "$TEST_DIR/tools/$command"
done
chmod +x "$TEST_DIR/tools/"*

reset_fixture() {
    rm -rf "$ROOT"
    rm -f "$MOCK_LOG.swap-failed"
    : > "$MOCK_LOG"
    mkdir -p "$ROOT/bin"
    printf 'previous binary\n' > "$ROOT/bin/previous"
}
run_install() { bash "$SOURCE/install-sessionmemory.sh" "$@" > "$TEST_DIR/output" 2>&1; }

for MOCK_OS in Darwin Linux; do
export MOCK_OS
if [ "$MOCK_OS" = Darwin ]; then ROOT="$HOME/Library/Application Support/LeannSessionMemory"; else ROOT="$XDG_DATA_HOME/LeannSessionMemory"; fi
reset_fixture
run_install
[ "$(cat "$MOCK_LOG")" = "$(printf 'index\nstart')" ]
[ -f "$ROOT/state/install.txt" ]
echo "PASS: $MOCK_OS initial index completes before service start and installation commit"

reset_fixture
if MOCK_FAIL_INDEX=1 run_install; then echo 'FAIL: indexing failure succeeded' >&2; exit 1; fi
[ "$(cat "$MOCK_LOG")" = index ]
[ "$(cat "$ROOT/bin/previous")" = 'previous binary' ]
[ ! -e "$ROOT/state/install.txt" ]
echo "PASS: $MOCK_OS indexing failure restores the previous binary and fails"

reset_fixture
if MOCK_RUNNING=1 MOCK_FAIL_INDEX=1 run_install; then echo 'FAIL: indexing failure succeeded' >&2; exit 1; fi
[ "$(cat "$MOCK_LOG")" = "$(printf 'index\nstart')" ]
[ "$(cat "$ROOT/bin/previous")" = 'previous binary' ]
echo "PASS: $MOCK_OS indexing failure restarts a previously running service"

rm -rf "$ROOT"
: > "$MOCK_LOG"
if MOCK_FAIL_INDEX=1 run_install; then echo 'FAIL: first indexing failure succeeded' >&2; exit 1; fi
[ ! -e "$ROOT/bin" ]
[ "$(cat "$MOCK_LOG")" = index ]
echo "PASS: $MOCK_OS first indexing failure removes the failed binary"

reset_fixture
if MOCK_RUNNING=1 MOCK_FAIL_SWAP=1 run_install; then echo 'FAIL: swap failure succeeded' >&2; exit 1; fi
[ "$(cat "$ROOT/bin/previous")" = 'previous binary' ]
[ "$(cat "$MOCK_LOG")" = start ]
echo "PASS: $MOCK_OS swap failure restores and restarts the previous installation"

rm -rf "$ROOT"
: > "$MOCK_LOG"
rm -f "$MOCK_LOG.swap-failed"
if MOCK_FAIL_SWAP=1 run_install; then echo 'FAIL: first swap failure succeeded' >&2; exit 1; fi
[ ! -e "$ROOT/bin" ]
[ ! -s "$MOCK_LOG" ]
echo "PASS: $MOCK_OS first swap failure leaves no binary"

for option in --no-start --no-service; do
    reset_fixture
    run_install "$option"
    [ ! -s "$MOCK_LOG" ]
    [ -f "$ROOT/state/install.txt" ]
    echo "PASS: $MOCK_OS $option skips indexing and service start"
done
done
