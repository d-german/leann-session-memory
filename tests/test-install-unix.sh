#!/usr/bin/env bash
# Installs for real on a disposable GitHub-hosted Linux or macOS runner, indexes a seeded conversation, searches it,
# reinstalls and rolls back. The service definition is written but not started: runners have no user service manager.
set -euo pipefail
if [ "${GITHUB_ACTIONS:-}" != true ]; then echo 'Run this test only on a disposable GitHub-hosted runner.' >&2; exit 1; fi

SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$(uname -s)" = Darwin ]; then ROOT="$HOME/Library/Application Support/LeannSessionMemory"; else ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/LeannSessionMemory"; fi
EXE="$ROOT/bin/SessionMemory"
TIMEOUT="${TIMEOUT_SECONDS:-900}"
CLAUDE="$HOME/.claude/CLAUDE.md"
COPILOT="$HOME/.copilot/copilot-instructions.md"
ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
WORK="${RUNNER_TEMP:-/tmp}"

# check "message" command args...: runs the command and stops the test when it fails.
check() {
    local message=$1
    shift
    if "$@"; then echo "PASS: $message"; else echo "FAIL: $message" >&2; exit 1; fi
}

sections() { grep -c 'leann-session-memory:start' "$1" || true; }
has_sections() { [ "$(sections "$1")" = "$2" ]; }
watcher_stopped() { ! pgrep -f "$ROOT/bin/app/SessionMemory.dll run" > /dev/null; }
data_hashes() { find "$ROOT/corpus" "$ROOT/.leann/indexes" -type f -exec shasum -a 256 {} + | sort; }

start_watcher() {
    nohup "$EXE" run > "$WORK/session-memory-watcher.out" 2>&1 < /dev/null &
}

wait_ready() {
    local deadline=$((SECONDS + TIMEOUT)) pid health
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ -f "$ROOT/state/watcher.json" ] && [ -f "$ROOT/state/archive-state.json" ]; then
            pid="$(jq -r '.process_id' "$ROOT/state/watcher.json" 2> /dev/null || echo 0)"
            health="$(curl -fsS --max-time 3 http://localhost:57392/health 2> /dev/null | jq -r '.indexesDir' 2> /dev/null || true)"
            if kill -0 "$pid" 2> /dev/null && [ "$health" = "$ROOT/.leann/indexes" ] && [ "$(jq '.errors | length' "$ROOT/state/watcher.json")" = 0 ]; then return 0; fi
        fi
        sleep 3
    done
    diagnose
    echo 'The watcher did not build and serve its first index before the timeout.' >&2
    return 1
}

diagnose() {
    {
        echo '--- processes'
        ps aux | grep -i -E 'dotnet|SessionMemory|leann' | grep -v grep || true
        echo '--- where the data went'
        find "$HOME" -maxdepth 7 \( -name 'watcher.json' -o -name 'watcher.log' -o -name 'LeannSessionMemory' \) -not -path '*/work/*' 2> /dev/null || true
        echo '--- state and logs'
        ls -laR "$ROOT/state" "$ROOT/logs" 2>&1 | head -40 || true
        cat "$ROOT/state/watcher.json" 2> /dev/null || true
        tail -n 30 "$ROOT/logs/watcher.log" 2> /dev/null || true
        echo '--- watcher output'
        cat "$WORK/session-memory-watcher.out" 2> /dev/null || true
        echo '--- status'
        "$EXE" status 2>&1 | head -30 || true
    } >&2
}

finds_seeded_session() {
    "$EXE" search 'Which release colour did the installer test choose?' --top-k 8 --json --include-current > "$WORK/search.json"
    jq -e --arg id "$ID" '[.results[] | select(.session_id == $id)] | length > 0' "$WORK/search.json" > /dev/null
}

shows_original_answer() { "$EXE" show "$ID" --around cobalt --chars 600 | grep -q 'cobalt orchard'; }

check_search() {
    check 'semantic search finds the seeded conversation' finds_seeded_session
    check 'show recovers the original answer' shows_original_answer
}

mkdir -p "$HOME/.copilot/session-state/$ID" "$HOME/.claude"
printf 'Keep my café instructions.\n' > "$CLAUDE"
{
    printf '{"type":"session.start","timestamp":"2026-10-27T00:00:00Z","data":{"sessionId":"%s","producer":"github-copilot-cli"}}\n' "$ID"
    printf '{"type":"user.message","id":"u1","timestamp":"2026-10-27T00:00:01Z","data":{"messageId":"u1","content":"Which release colour did the installer test choose?"}}\n'
    printf '{"type":"assistant.message","id":"a1","timestamp":"2026-10-27T00:00:02Z","data":{"messageId":"a1","phase":"final","content":"The installer test chose cobalt orchard as its release colour."}}\n'
} > "$HOME/.copilot/session-state/$ID/events.jsonl"

check 'model is absent before first installation' test ! -e "$HOME/.leann/models/facebook-contriever/model.onnx"
"$SOURCE/install-sessionmemory.sh" --no-start
check 'first installation downloads the model' test -f "$HOME/.leann/models/facebook-contriever/model.onnx"
check 'first installation writes the SessionMemory launcher' test -x "$EXE"
check 'first installation writes the private LEANN launcher' test -x "$ROOT/leann/leann-dotnet"
check 'one memory section in the existing Claude folder' has_sections "$CLAUDE" 1
check 'one memory section in the existing Copilot folder' has_sections "$COPILOT" 1
check 'an assistant that is not installed is skipped' test ! -e "$HOME/.codex/AGENTS.md"
if [ "$(uname -s)" = Darwin ]; then
    check 'launchd agent definition is valid' plutil -lint "$HOME/Library/LaunchAgents/com.leannsessionmemory.refresh.plist"
else
    check 'systemd unit sets a memory limit' grep -q '^MemoryMax=' "$HOME/.config/systemd/user/leann-session-memory.service"
    check 'the search command is unquoted, so a permission rule can match it' grep -q "^$EXE search " "$CLAUDE"
fi

start_watcher
wait_ready
check 'initial instruction backup exists' test -n "$(find "$ROOT/backups" -name '*CLAUDE.md.bak')"
check_search
check 'installed launcher passes self-test' "$EXE" self-test

"$SOURCE/install-sessionmemory.sh" --no-service
check 'reinstall keeps one memory section' has_sections "$CLAUDE" 1
check 'reinstall stopped the running watcher' watcher_stopped
start_watcher
wait_ready
check_search

printf 'Added later: Ω.\n' >> "$CLAUDE"
BEFORE="$(data_hashes)"
check 'corpus and indexes are populated' test -n "$BEFORE"
"$SOURCE/install-sessionmemory.sh" --rollback
check 'rollback removes the integration state' test ! -e "$ROOT/state/install.txt"
check 'rollback stops the watcher' watcher_stopped
check 'rollback keeps the original instructions' grep -q 'Keep my café instructions' "$CLAUDE"
check 'rollback keeps later edits' grep -q 'Added later: Ω' "$CLAUDE"
check 'rollback removes the memory section' has_sections "$CLAUDE" 0
check 'rollback removes an instruction file created only for memory' test ! -e "$COPILOT"
check 'rollback preserves the corpus and indexes' test "$(data_hashes)" = "$BEFORE"
check_search
"$SOURCE/install-sessionmemory.sh" --rollback
check 'a repeated rollback succeeds' test ! -e "$ROOT/state/install.txt"
echo 'All checks passed.'
