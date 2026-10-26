# LEANN Session Memory

A local, searchable memory of your AI coding sessions. It indexes your Codex, Claude Code, GitHub Copilot (CLI and
desktop) and VS Code Copilot Chat conversations with [LEANN .NET](https://github.com/d-german/leann-dotnet), keeps
the index current in the background, and tells those assistants how to search it. A new session can then find where
an old one left off instead of starting from a blank memory.

Everything runs on your machine: transcripts are read from each tool's local storage, embedded with a local model and
searched locally.

## Requirements

- Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- About 650 MB for LEANN .NET and the `facebook/contriever` model, plus the index (about 250 MB for 50 MB of
  conversation text).
- A GPU helps: the first full index of 50 MB of text took about 19 minutes on a laptop GPU.

## Install

```powershell
git clone https://github.com/d-german/leann-session-memory
cd leann-session-memory
.\Install-SessionMemory.ps1
```

The installer:

- builds the app into `%LOCALAPPDATA%\LeannSessionMemory\bin`;
- installs its own copy of LEANN .NET and downloads the model if it's missing;
- registers a hidden scheduled task, `LeannSessionMemoryRefresh`, that keeps the background watcher running;
- adds a short "Local LEANN session memory" section to `~\.codex\AGENTS.md`, `~\.claude\CLAUDE.md` and
  `~\.copilot\copilot-instructions.md`, keeping backups of the originals.

The first index builds in the background. `.\Install-SessionMemory.ps1 -Rollback` removes the task and the memory
section from the instruction files, keeping any other edits; the index is kept.

## Use

Assistants run these on their own; you can too.

```powershell
$memory = "$env:LOCALAPPDATA\LeannSessionMemory\bin\SessionMemory.exe"
& $memory search "what did we decide about the retry limit"   # by meaning
& $memory find RabbitMQ threat                                 # exact terms as whole words, including file edits and commands
& $memory find RAM --partial                                   # substring matching, e.g. also "param"
& $memory find RabbitMQ threat --session <session id>          # every match inside one session
& $memory find appsettings --current --since 2026-10-26T09:00  # every match in this session since a time
& $memory show <session id> --last 15                          # a session's latest messages
& $memory show <session id> --around "T15|T22"                 # messages matching a pattern
& $memory show --current --around "dotnet test"                # the same, in the session running the command
& $memory exclude <session id>                                 # drop a session from the indexes
& $memory status
```

`search` and `find` print compact text grouped by session; add `--json` for machine-readable output.

`search` works best with one plain natural-language question describing what happened ("What hardware specs did I
request for my new Linux VM?"), not a list of keywords; if the top results miss, rephrase and search again. Use `find`
only for exact names, IDs, numbers, paths or error text.

`Search-SessionMemory.ps1`, the wrapper the instructions point assistants to, takes `-TopK`, `-Current`,
`-IncludeCurrent` and `-Json` for the matching `search` options.

### Recall the current session

`search` and `find` leave out the session running them, so a conversation doesn't find its own questions;
`--include-current` keeps it in the results. `--current` does the opposite and looks only at that session:
`find --current` lists every matching excerpt in it, newest first, and `show --current` reads it. With `find`,
`--since <time>` (ISO 8601) keeps only activity at or after that time, and `--outputs` also searches tool results
(GitHub Copilot CLI).

After a context compaction, an assistant can use these to recover the exact commands, decisions, errors and file paths
from earlier in its own conversation instead of guessing, re-reading files or asking again. The running session is
detected from `COPILOT_AGENT_SESSION_ID` for GitHub Copilot CLI; other clients are detected when they expose a
session ID.

## How it works

- Every minute the watcher re-reads the clients whose files changed and writes one Markdown transcript per session:
  user and assistant messages, the session title and timestamps. Tool outputs and injected system context are left out.
- Changed sessions go into a small "recent" LEANN index that rebuilds in seconds once a session has been quiet for
  90 seconds. The full archive rebuilds after about 4 MB of changes. One query ranks both.
- A resident `leann-dotnet --serve` process keeps searches at about a second.
- `find` scans the raw logs in parallel, including tool inputs that never reach the transcripts, and Copilot CLI tool
  results with `--outputs`. A term matches only as a whole word (`RAM` doesn't match `param`) unless `--partial` is
  given, and records that run the memory tools themselves are skipped, so a session's own searches don't rank it first.
- Each assistant is one `ISessionClient` implementation, registered with Microsoft.Extensions.DependencyInjection; each
  command is one `ICommand` class under `Commands\`. See [docs/client-strategy.md](docs/client-strategy.md).

## Privacy

Nothing is redacted: credentials that appear in your sessions are indexed too. The data stays on your machine, but
results an assistant retrieves become part of that assistant's prompt.

## Limits

- Windows only.
- Memory only knows what a session recorded; check live systems for their current state.
- The scheduled task runs only on AC power, the Windows default.

## License

MIT
