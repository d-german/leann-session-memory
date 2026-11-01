#!/usr/bin/env bash
# Installs LEANN Session Memory on Linux (systemd user service) or macOS (launchd agent).
# Usage: ./install-sessionmemory.sh [--rollback] [--no-service] [--no-start]
#   --rollback    stop the watcher and remove the memory section from the instruction files; the index is kept
#   --no-service  install without a background service (run "SessionMemory run" yourself)
#   --no-start    write the service definition without starting it
# Optional environment: SESSIONMEMORY_CPU_QUOTA (default 200%, Linux), SESSIONMEMORY_MEMORY_HIGH_PERCENT (40) and
# SESSIONMEMORY_MEMORY_MAX_PERCENT (60) bound the watcher on small machines.
set -euo pipefail

SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The default data folder of the app is .NET's LocalApplicationData: XDG_DATA_HOME or ~/.local/share on Linux, but
# ~/Library/Application Support on macOS.
case "$(uname -s)" in
    Linux) OS=linux; ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/LeannSessionMemory" ;;
    Darwin) OS=macos; ROOT="$HOME/Library/Application Support/LeannSessionMemory" ;;
    *) echo "Unsupported system: $(uname -s). Use Install-SessionMemory.ps1 on Windows." >&2; exit 1 ;;
esac
# A private LEANN .NET, so the global tool and its running servers are never touched.
# 2.10 or later is required: it ranks the recent and archive indexes together in one search.
LEANN_VERSION='2.10.1'
BIN="$ROOT/bin"
LEANN_DIR="$ROOT/leann"
LEANN_TOOL="$ROOT/leann-tool"
MANIFEST="$ROOT/state/install.txt"
START_MARK='<!-- leann-session-memory:start -->'
END_MARK='<!-- leann-session-memory:end -->'
LABEL='com.leannsessionmemory.refresh'
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
UNIT='leann-session-memory.service'
UNIT_FILE="$HOME/.config/systemd/user/$UNIT"
TARGETS=("$HOME/.codex/AGENTS.md" "$HOME/.claude/CLAUDE.md" "$HOME/.copilot/copilot-instructions.md")
TAB=$'\t'

ROLLBACK=0
SERVICE=1
START_SERVICE=1
for argument in "$@"; do
    case "$argument" in
        --rollback) ROLLBACK=1 ;;
        --no-service) SERVICE=0 ;;
        --no-start) START_SERVICE=0 ;;
        *) echo "Usage: $0 [--rollback] [--no-service] [--no-start]" >&2; exit 2 ;;
    esac
done

quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

strip_section() {
    awk -v s="$START_MARK" -v e="$END_MARK" 'index($0, s) { skip = 1 } !skip { print } index($0, e) { skip = 0 }' "$1"
}

trim_trailing_blank() {
    awk '{ lines[NR] = $0 } END { n = NR; while (n > 0 && lines[n] ~ /^[[:space:]]*$/) n--; for (i = 1; i <= n; i++) print lines[i] }'
}

is_blank() { [ -z "$(printf '%s' "$1" | tr -d '[:space:]')" ]; }

stop_service() {
    if [ "$OS" = linux ]; then
        systemctl --user disable --now "$UNIT" >/dev/null 2>&1 || true
    else
        launchctl bootout "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || launchctl unload "$PLIST" >/dev/null 2>&1 || true
    fi
    # Processes started from this install, including a watcher started by hand and the search server it launched.
    pkill -f "$BIN/app/SessionMemory.dll run" >/dev/null 2>&1 || true
    pkill -f "$LEANN_TOOL/leann-dotnet" >/dev/null 2>&1 || true
    sleep 1
}

remove_service_definition() {
    rm -f "$UNIT_FILE" "$PLIST"
    if [ "$OS" = linux ] && systemctl --user show-environment >/dev/null 2>&1; then systemctl --user daemon-reload || true; fi
}

if [ "$ROLLBACK" = 1 ]; then
    stop_service
    remove_service_definition
    if [ -f "$MANIFEST" ]; then
        # Only the memory section is removed, so edits made since the install are kept.
        while IFS="$TAB" read -r kind path flag; do
            [ "$kind" = file ] && [ -f "$path" ] || continue
            remaining="$(strip_section "$path" | trim_trailing_blank)"
            if ! is_blank "$remaining"; then printf '%s\n' "$remaining" > "$path"
            elif [ "$flag" = existed ]; then : > "$path"
            else rm -f "$path"
            fi
        done < "$MANIFEST"
        rm -f "$MANIFEST"
    fi
    echo 'Session-memory instructions and background service removed. Corpus and indexes were preserved.'
    exit 0
fi

find_dotnet() {
    local candidate
    for candidate in "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet \
        /opt/homebrew/share/dotnet/dotnet /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet; do
        if [ -n "$candidate" ] && [ -x "$candidate" ] && "$candidate" --list-sdks 2>/dev/null | grep -q '^10\.'; then
            DOTNET="$candidate"
            return 0
        fi
    done
    echo 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0' >&2
    return 1
}

find_dotnet
# The apphost of a framework-dependent tool finds the runtime only through DOTNET_ROOT unless .NET is installed system-wide.
DOTNET_ROOT_DIR="$("$DOTNET" --list-runtimes | awk '/^Microsoft\.NETCore\.App 10\./ { sub(/^[^[]*\[/, ""); sub(/\/shared\/Microsoft\.NETCore\.App\].*$/, ""); print; exit }')"
[ -n "$DOTNET_ROOT_DIR" ] || DOTNET_ROOT_DIR="$(dirname "$DOTNET")"

write_wrapper() {
    printf '#!/bin/sh\nDOTNET_ROOT=%s\nexport DOTNET_ROOT\nexec %s "$@"\n' "$(quote "$DOTNET_ROOT_DIR")" "$2" > "$1"
    chmod 755 "$1"
}

BACKUPS="$ROOT/backups/$(date +%Y%m%d%H%M%S)"
STAGE="$ROOT/staging"
CHANGED=''
SWAPPED=0
DONE=0
SERVICE_STOPPED=0
WAS_RUNNING=0
WAS_ENABLED=0

restore_files() {
    local path state backup
    [ -n "$CHANGED" ] || return 0
    while IFS="$TAB" read -r path state backup; do
        [ -n "$path" ] || continue
        if [ "$state" = created ]; then rm -f "$path"; else cp "$backup" "$path"; fi
    done <<EOF
$CHANGED
EOF
}

on_exit() {
    local status=$?
    if [ "$DONE" = 1 ]; then exit "$status"; fi
    echo 'Install failed; restoring the previous installation.' >&2
    if [ "$SERVICE_STOPPED" = 1 ]; then stop_service; fi
    if [ "$SWAPPED" = 1 ]; then
        rm -rf "$BIN"
        if [ -d "$ROOT/bin.previous" ]; then mv "$ROOT/bin.previous" "$BIN"; fi
    fi
    restore_files
    if [ "$SERVICE_STOPPED" = 1 ]; then
        if [ "$OS" = linux ]; then
            systemctl --user daemon-reload || true
            if [ "$WAS_ENABLED" = 1 ]; then systemctl --user enable "$UNIT" || true; fi
            if [ "$WAS_RUNNING" = 1 ]; then systemctl --user start "$UNIT" || echo 'Could not restart the previous watcher.' >&2; fi
        elif [ "$WAS_RUNNING" = 1 ]; then
            launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null || launchctl load -w "$PLIST" || echo 'Could not restart the previous watcher.' >&2
        fi
    fi
    rm -rf "$STAGE"
    exit "$status"
}
trap on_exit EXIT

mkdir -p "$ROOT/state"
chmod 700 "$ROOT"

# Build and fetch everything first, so a failure leaves the running installation untouched.
rm -rf "$STAGE"
mkdir -p "$STAGE"
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET" publish "$SOURCE/SessionMemory.csproj" --configuration Release \
    --output "$STAGE/app" --nologo --verbosity quiet
write_wrapper "$STAGE/SessionMemory" "$(quote "$DOTNET") \"\$(dirname \"\$0\")/app/SessionMemory.dll\""

if [ -x "$LEANN_TOOL/leann-dotnet" ]; then LEANN_VERB=update; else LEANN_VERB=install; fi
"$DOTNET" tool "$LEANN_VERB" leann-dotnet --version "$LEANN_VERSION" --tool-path "$LEANN_TOOL" --configfile "$SOURCE/nuget.config" > /dev/null
mkdir -p "$LEANN_DIR"
write_wrapper "$LEANN_DIR/leann-dotnet" "$(quote "$LEANN_TOOL/leann-dotnet")"

if [ ! -f "$HOME/.leann/models/facebook-contriever/model.onnx" ]; then
    "$LEANN_DIR/leann-dotnet" --setup --model facebook/contriever
fi

if [ "$OS" = linux ]; then
    systemctl --user is-active --quiet "$UNIT" && WAS_RUNNING=1
    systemctl --user is-enabled --quiet "$UNIT" && WAS_ENABLED=1
else
    if launchctl print "gui/$(id -u)/$LABEL" 2>/dev/null | grep -q 'state = running'; then WAS_RUNNING=1; fi
fi
stop_service
SERVICE_STOPPED=1
rm -rf "$ROOT/bin.previous"
if [ -d "$BIN" ]; then mv "$BIN" "$ROOT/bin.previous"; fi
SWAPPED=1
mv "$STAGE" "$BIN"
EXE="$BIN/SessionMemory"

if [ "$SERVICE" = 1 ] && [ "$START_SERVICE" = 1 ]; then
    "$EXE" run --once --progress
fi

# read, not $(cat <<EOF): bash 3.2 on macOS mis-parses apostrophes in a here-document inside $(...).
IFS= read -r -d '' BLOCK <<'EOF' || true
<!-- leann-session-memory:start -->
## Local LEANN session memory

Before repeating an investigation, or when prior decisions, failed attempts, corrections, or unfinished work may matter, run:

```bash
"@EXE@" search "<specific question>"
```

Search combines SQLite FTS5 Porter BM25 over overlapping windows of logical neighboring messages with LEANN semantic windows, fused with RRF at 60. It returns up to 8 windows by default (up to 50 with `--top-k`) within a 12,000-character actual-text budget. Results cite original messages with role and timestamp; one message may produce multiple windows. Search runs locally without an LLM.

For a multi-term lexical match, the topic and another positive query term must share one source passage. Semantic-ranked windows matched to original messages may also qualify when the topic appears anywhere in the message, or when no query words overlap, as with a paraphrase. Generic qualifier-only matches are dropped.

For latest or current-status questions, add `--latest`. Message dates inform ranking alongside relevance, but results do not guarantee completeness or establish current state. Use `--session <id>` to narrow a search to one session (at least 8 characters; it conflicts with `--current`). `--exclude-topic <literal phrase>` filters only that literal phrase when requested; ordinary question wording does not imply negation, so comparisons can retain both sides.

For latest/history navigation, run `"@EXE@" timeline <literal-term> [<term> ...]` with actual subject terms. It scans visible cached originals using literal OR matching, not relevance ranking; a term match does not prove the session did the work. It reports UTC dates, undated status, result counts, truncation and stale-cursor markers. `--top` defaults to 12 (max 50); `--cursor` is opaque; `--include-current` and `--json` are available. Matching case-folds invariantly but does not normalize Unicode composition, so code-point sequences must match.

Use hierarchical source reading before answering: run a plain question or timeline to identify candidate sessions, then search for the specific fact within each relevant session: `"@EXE@" search "<specific aspect>" --session <id>`. Initial windows are leads, not a full answer. Follow a navigation result's singular `read_hint` command as printed; it may include `--offset`. Search results' `read_context` starts at the original message beginning and has no offset. Read a main citation with `"@EXE@" show <id> --message <positive original message number> --offset <n> --context 1 --chars 6000`. The joined original message preserves blank lines and includes its UTC timestamp. Offsets are nonnegative UTF-16 character units and work only with `--message`. Exact-read output shows `[offset N; next offset N/end; total N UTF-16 units]`; the displayed offset may move back to a word or surrogate boundary, and an offset past the message errors. Continue with the displayed next offset if clipped. `--message` conflicts with `--around`; an exact message miss exits with 2. `--context` accepts 0 to 5 and `--chars` accepts 200 to 20,000. If output has an omission marker, continue reading, refine the search, or say the evidence is unknown; never assume clipped text is complete. `--around` and `--last` remain available.

To locate an indirect update, run `"@EXE@" related <source-id> --message <number> --top <n>` on a specific cited source message. It follows one hop of explicit HTTP(S) URIs or full known session IDs and returns referring/target citations plus visible known-session navigation. URI path, query and fragment must match exactly; a focused comment URI is not a base-page alias. It does not infer branches or topics or fetch the web. Treat a shared reference as a navigation hint, not proof of shared work; do not open every related session wholesale.

If a lookup or summary names an earlier work session, search that session or title and attribute the work to the source session, not the reporter. Distinguish reporting from doing. For date-bound questions, compare relevant message timestamps in UTC, not session last activity; do not use a future event to answer a past-state question or assert global latest without adequate coverage. Answer only the material facts requested. A proposal, intention or request does not prove the action was completed.

For searches tied to tracked work, include the ticket ID and full PR URL when relevant; search does not follow issue, PR or branch links automatically. If results are insufficient, refine the query or run another search. Cite status claims with source session, message and date. Distinguish proposals, patches, merges, publication and verification, and say when evidence is insufficient.

For verification, keep the UTF-8 JSON schema `{ "claims": [{ "claim": "...", "session_id": "...", "message_number": 1, "quote": "..." }] }` unchanged. Submit at most 12 claims in a file no larger than 64 KiB with `"@EXE@" verify <claims-json-file>`. It checks quote presence and returns context within 12,000 source characters; a matching quote does not prove meaning or truth. Review the source yourself.

Use one plain natural-language question describing what happened (for example, "What hardware specs did I request for my new Linux VM?"). If results are insufficient, rephrase or run a focused follow-up search. Use `--latest` when recency matters.

Search results show source message dates, roles and citations. To read more, run `"@EXE@" show <session id> --last 15` or `--around "<regex>"`; do not open transcript files directly.

Use `find` only for exact names, IDs, numbers, paths or error text that may sit in file edits, commands or page updates rather than in the conversation: run `"@EXE@" find <term> [<term> ...]`. It lists sessions where every term appears as a whole word, newest first, with one excerpt each; add `--partial` to match inside words, or `--session <session id>` to see every matching excerpt in one session. It costs more than search, so skip it for questions about status, decisions or reasons.

Use chronology and explicit corrections. Compare source-message timestamps, not session last activity or file modification time. A newer message may still be a reposted summary; inspect direct source work before treating it as current. Cite status claims with source session, message and date. Searches leave out your current conversation by default. After context compaction, use `"@EXE@" find ... --current` or `"@EXE@" show --current --around "<regex>"` to recover exact details. Keep context bounded.

If the shell does not allow this command in the current session, continue without it and do not retry.
<!-- leann-session-memory:end -->
EOF
BLOCK="${BLOCK%$'\n'}"
# A bare path lets an assistant's permission rule such as shell(<path>:*) match the command; quote it only when it must be.
case "$EXE" in *[!A-Za-z0-9_./-]*) COMMAND="\"$EXE\"" ;; *) COMMAND="$EXE" ;; esac
BLOCK="${BLOCK//\"@EXE@\"/$COMMAND}"

# Assistants that are not installed (no config folder yet) are skipped; run the installer again after installing one.
MANIFEST_LINES=''
for target in "${TARGETS[@]}"; do
    previous=''
    [ -f "$MANIFEST" ] && previous="$(awk -F"$TAB" -v p="$target" '$1 == "file" && $2 == p { print $3 }' "$MANIFEST")"
    if [ -f "$target" ]; then
        state="${previous:-existed}"
        mkdir -p "$BACKUPS"
        folder="$(basename "$(dirname "$target")")"
        backup="$BACKUPS/${folder#.}-$(basename "$target").bak"
        cp "$target" "$backup"
        CHANGED="$CHANGED$target${TAB}existed${TAB}$backup"$'\n'
        body="$(strip_section "$target" | trim_trailing_blank)"
    elif [ -d "$(dirname "$target")" ]; then
        state=created
        CHANGED="$CHANGED$target${TAB}created${TAB}"$'\n'
        body=''
    else
        continue
    fi
    if is_blank "$body"; then printf '%s\n' "$BLOCK" > "$target"; else printf '%s\n\n%s\n' "$body" "$BLOCK" > "$target"; fi
    MANIFEST_LINES="${MANIFEST_LINES}file${TAB}${target}${TAB}${state}"$'\n'
done

write_linux_unit() {
    local total_kb high max
    total_kb="$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo)"
    high=$((total_kb * ${SESSIONMEMORY_MEMORY_HIGH_PERCENT:-40} / 100 / 1024))
    max=$((total_kb * ${SESSIONMEMORY_MEMORY_MAX_PERCENT:-60} / 100 / 1024))
    mkdir -p "$(dirname "$UNIT_FILE")"
    cat > "$UNIT_FILE" <<EOF
[Unit]
Description=LEANN Session Memory watcher

[Service]
ExecStart="$EXE" run
Restart=always
RestartSec=60
Nice=19
CPUQuota=${SESSIONMEMORY_CPU_QUOTA:-200%}
MemoryHigh=${high}M
MemoryMax=${max}M

[Install]
WantedBy=default.target
EOF
    if ! systemctl --user show-environment >/dev/null 2>&1; then
        echo 'No systemd user session: the service definition was written but not started. Run "SessionMemory run" yourself.' >&2
        return 0
    fi
    systemctl --user daemon-reload
    systemctl --user enable "$UNIT" >/dev/null 2>&1
    # Without lingering, the watcher stops when the last login session ends.
    loginctl enable-linger "$(id -un)" >/dev/null 2>&1 || echo 'Could not enable lingering: the watcher stops when you log out.' >&2
    if [ "$START_SERVICE" = 1 ]; then systemctl --user restart "$UNIT"; fi
}

write_macos_agent() {
    mkdir -p "$(dirname "$PLIST")" "$ROOT/logs"
    cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$LABEL</string>
    <key>ProgramArguments</key><array><string>$EXE</string><string>run</string></array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>ThrottleInterval</key><integer>60</integer>
    <key>ProcessType</key><string>Background</string>
    <key>Nice</key><integer>10</integer>
    <key>LowPriorityBackgroundIO</key><true/>
    <key>StandardOutPath</key><string>$ROOT/logs/launchd.log</string>
    <key>StandardErrorPath</key><string>$ROOT/logs/launchd.log</string>
</dict>
</plist>
EOF
    if [ "$START_SERVICE" = 1 ]; then
        launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null || launchctl load -w "$PLIST"
    fi
}

if [ "$SERVICE" = 1 ]; then
    if [ "$OS" = linux ]; then service_file="$UNIT_FILE"; else service_file="$PLIST"; fi
    if [ -f "$service_file" ]; then
        mkdir -p "$BACKUPS"
        cp "$service_file" "$BACKUPS/service-definition"
        CHANGED="$CHANGED$service_file${TAB}existed${TAB}$BACKUPS/service-definition"$'\n'
    else
        CHANGED="$CHANGED$service_file${TAB}created${TAB}"$'\n'
    fi
    if [ "$OS" = linux ]; then write_linux_unit; else write_macos_agent; fi
fi

printf '%s' "$MANIFEST_LINES" > "$MANIFEST"
rm -rf "$ROOT/bin.previous"
DONE=1
echo "Installed to $ROOT"
echo "Check progress with: \"$EXE\" status"
