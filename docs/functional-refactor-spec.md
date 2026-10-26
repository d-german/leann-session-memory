# Functional Refactor Specification: SessionMemory

Status: **implemented** (WP0-WP6 and most of WP7) on 2026-10-26, in the commit after `b549afd`. The current-session features
(`--current`, `--since`, `--outputs`, Codex/Claude session detection) landed first in `b549afd`.
Baseline for the audit: commit `b7339e8`. Line numbers below are approximate for that commit; search by symbol name.
Source: six-worker read-only audits run on 2026-10-26 (Result/railway audit, records/ImmutableArray audit).

Decisions taken: D1-D3, D5 and D6 used the recommended defaults. D4: converted and new records use explicit constructors or init-only
properties, never positional syntax (the pre-existing positional `Segment` and `Snippet` records were left as they were).
Verification at completion: Rider build clean, self-test passing (with per-area `SelfTest.*.cs` checks), zero actionable Rider findings,
1220 of 1222 real-data documents byte-identical to the pre-refactor capture (the other two were sessions still being written),
identical family stats and manifest key order, CLI probe outputs identical except the D2 message, and the replay characterization
checks passing on both the old and the new code. Production code went from 26 catch blocks to 10 (all boundary adapters or kept
policies) and from 22 throw statements to 5.
Not done from WP7: the optional `CliOptions` parser and named record structs for the `Query`/`RunLeann` tuples where they were not
needed (`RunLeann` now returns a `LeannOutput` record).

This document is self-contained. A new session should be able to pick a work package (WP), implement it, verify it and stop,
without re-running the audits.

## 1. Goals

1. **Railway-oriented programming** with [CSharpFunctionalExtensions](https://github.com/vkhorikov/CSharpFunctionalExtensions)
   (`Result`, `Result<T>`, `Result<T,E>`, `Maybe<T>`, `Bind`, `Map`, `Ensure`, `Tap`, `TapError`, `Match`, `Result.Try`).
   Remove `try/catch` wherever the failure is an expected outcome; wrap unavoidable boundary exceptions **once** at the edge.
2. **Immutable data**: read-only records instead of mutable classes where the type is a value object;
   `ImmutableArray<T>` instead of `IReadOnlyList<T>`/`List<T>`/`T[]` where a collection is built once and then only read.
3. **A demo**: `exclude` and `show` should show the railway clearly (see section 8, "Demo track").
4. **Zero behavior change** unless a decision in section 3 says otherwise.

Non-goals: new features, performance work, changing the on-disk formats, changing the installed tool's behavior.

## 2. Working rules (apply to every WP)

- **Code edits go through Rider MCP** (`rider-apply_patch`, `rider-get_file_problems`, `rider-lint_files`,
  `rider-build_solution_start`/`_state`). Always pass `rootFolder` = `C:/Users/dgerman/Documents/Codex/2026-10-20/text-help-me-install-and-configure`
  (the **workspace root**; the git repo is its `SessionMemory` subfolder). Stop and report if Rider MCP is unavailable.
  The user-level instructions are in `C:\Users\dgerman\.codex\AGENTS.md`.
- Use backslash paths with `get_file_problems` (forward slashes can return an empty list); `lint_files` accepts either.
- **Never suppress anything**: no `NoWarn`, no `#pragma warning disable`, no `[SuppressMessage]`, no lowering severity, no file exclusions.
- **No primary constructors on classes.** Positional `record` syntax is not a class primary constructor, but see decision D4.
- **No Python dependency.** Services are .NET console apps; scripts are PowerShell.
- **Do not commit or push** unless the user asks for it in that session.
- Modifier order follows the user's Rider setting: `sealed internal class`, `sealed private class`, `private static readonly`.
  Block-scoped namespaces (`namespace SessionMemory { ... }`). No trailing commas in initializers/collections. Do not "fix" these.
- One build at a time in Rider. Workers must not build; the integrator builds.
- If a tool shows a line of `SelfTest.cs` containing a masked URL (`******`), that line holds a deliberate fake `user:pass@` test URL.
  **Never include that line in a patch.** After edits, confirm no `\*{4,}` sequence exists in any `.cs` file.
- Play the completion beep **once**, as the very last tool call before the final reply:
  `powershell -NoLogo -Command "[console]::Beep(2500, 1000)"`. Sub-agents must not beep.
- Parallel work: give each worker **exclusive ownership of its files** (section 9 has an ownership map).

## 3. Decisions needed before the affected WP starts

Each has a recommended default. If the user is unavailable, take the default and record it in the commit message or PR.

| ID | Question | Recommended default | Blocks |
|---|---|---|---|
| D1 | `Json.IntOr` on a non-numeric string/array/object currently throws `FormatException`, which fails the **whole family** parse. Keep, or skip that value? | **Keep** (matches the original Python `int()`), but make it explicit with `long.TryParse` and the same failure surfaced through `Result` at the family boundary. | WP6 |
| D2 | CLI bad number (`--top-k x`) message currently is .NET's text plus `Usage`, exit 2. | New text: `'x' is not a whole number for --top-k.` + `\n` + `Usage`, exit **2** unchanged. | WP4 |
| D3 | Copilot store/desktop I/O failures: `CopilotDesktopIds` swallows query errors but not open failures; other opens fail the whole family. One policy? | **Fail the family** for open failures, tolerate a missing optional table only (explicit probe, not a catch). | WP6 |
| D4 | Records: positional (`record Message(string Id, ...)`) or init-only with explicit shape? | **Init-only with initializers for anything JSON-serialized; positional for non-serialized value objects.** Positional record syntax is not a class primary constructor, but the user dislikes primary constructors, so confirm. | WP1 |
| D5 | `Result.Try` catches **every** exception; many current catches are filtered (`IOException or UnauthorizedAccessException`). | Add one small adapter (`Attempt.Io<T>`, `Attempt.Json<T>`) that keeps the filter. Use plain `Result.Try` only at top-level boundaries that already catch everything. | WP0 |
| D6 | Package version. | `CSharpFunctionalExtensions` **3.6.0** (same as the user's Deidentify library in SleepEditWeb). Confirm it restores through `nuget.config`. | WP0 |

## 4. Behavior contract (must not change)

1. **JSON on disk.** `Store.Options` uses `SnakeCaseLower` + indented output. Existing files under `%LOCALAPPDATA%\LeannSessionMemory\state\`
   (`manifest.json`, `archive-state.json`, `recent-state.json`, `watcher.json`, `excluded.json`) must still load, and written files keep the same
   property names and **key order**. Never turn the `Dictionary` fields of `Manifest`, `FamilyReport.Stats` or `FamilyResult.Stats` into
   `ImmutableDictionary`/`FrozenDictionary` (hash order would reorder keys and churn the manifest; `FrozenDictionary` cannot be deserialized).
2. **Strings other code depends on.** `FamilyReport.Error` is `"{ExceptionType}: {Message}"` and is persisted. The watcher persists/logs failure text
   in `watcher.json`. Keep `Type: Message` formats.
3. **Exit codes and messages** of every CLI command (table in section 7.1).
4. **Streaming.** `Jsonl.Read`, `Jsonl.ReadNodes`, `Parsers.Rglob`, `WalkDirectories`, `SqliteReader.Query` stay lazy `yield` iterators.
   Never return `Result<IEnumerable<T>>`/arrays from them (inputs are GBs).
5. **Order.** Message order, document order, dictionary insertion order used for output, `sequence++` order in `CopilotEventMessages`.
6. **Timing/atomicity.** `Store.Retry` (10 attempts, 500 ms), temp-file-then-move writes, `Indexer.Publish` rollback order
   (restore previous, then rethrow, then delete previous), `Build`'s `finally` cleanup.
7. **Hot paths** (`Finder.Scan*`, `Scan.Note`, `Json` extension helpers `Get/Str/StrOr/IntOr/AsString`) allocate nothing new per line.
8. **Current-session exclusion** (already shipped): `search`/`find` skip `COPILOT_AGENT_SESSION_ID` unless `--include-current`; `find --session` still reaches it.
9. **Stats counters** (`malformed`, `partial_trailing`, `operations_failed`, ...) are incremented in the same places and order.

## 5. Inventory and dispositions

### 5.1 try/catch/finally (26 catches, 3 finally-only)

Legend: **A** = expected outcome used as control flow, replace. **B** = unavoidable boundary exception, wrap once at the edge.
**C** = invariant/programmer error, keep. **D** = cleanup/policy, keep.

| File | Site (approx line) | Cat | Disposition | WP |
|---|---|---|---|---|
| Program.cs | `Main` catch `FormatException or OverflowException` (~36) | A | `int.TryParse` helpers returning `Result<int,CliError>`; delete catch | 4 |
| Program.cs | `Show` catch `InvalidOperation/Argument/RegexMatchTimeout` (~110) | A/B | `FindSession` returns `Result`; regex construction/timeout wrapped once; delete catch | 4 |
| Compact.cs | `throw InvalidOperationException` no match / several match (~153, ~155) | A | `Result<(string,DocumentMeta),CliError>` | 4 |
| Compact.cs | `File.ReadAllText` unguarded (~114) | B | wrap with `Attempt.Io`; fixes a latent unhandled crash | 4 |
| Compact.cs | `int.Parse` of regex `\d+` (~52) | C | `int.TryParse` hardening only | 4 |
| Corpus.cs | `ParseFamily` catch `Exception` (~147) | B | `Result.Try(family.Parse, ex => $"{ex.GetType().Name}: {ex.Message}")` then `Match` to `FamilyReport`; keep partial documents behavior | 5 |
| Corpus.cs | `Store.Load<T>` catch (~276) | B | `Attempt.Io+Json` returning `Result<T>`; keep `LoadOrDefault` (null on failure) wrappers because callers tolerate missing/locked/corrupt | 5 |
| Corpus.cs | `Store.LoadNode` catch (~289) | B | share one private `ReadJson` edge with `Load` | 5 |
| Corpus.cs | `Store.Retry` catch `when (attempt < 10 ...)` (~314) | D | **keep** (retry policy) | - |
| Indexer.cs | `Search` `?? throw new JsonException()` + catch (~159/161) | A | `Maybe.From(...).ToResult(...)`; plain null check, no throw/catch | 5 |
| Indexer.cs | `throw FileNotFoundException` LEANN/model missing (~103) | A | `Result.Failure`; keep text prefix `FileNotFoundException:` where the watcher logs it | 5 |
| Indexer.cs | `throw InvalidOperationException` rebuild exit != 0 (~123) | A | `RunLeann(...).Tap(writeLog).Ensure(exit==0, ...)` | 5 |
| Indexer.cs | `throw InvalidOperationException` canary validation (~259) | A | `Ensure` in the Validate step | 5 |
| Indexer.cs | `Process.Start(...) ?? throw` (~314) | B/C | `Maybe.From(Process.Start).ToResult(...)` + `Win32Exception` wrapped | 5 |
| Indexer.cs | `Publish` try/catch + `throw;` (~273-281) | B/D | keep rollback order; may use `TapError(restore)`; **highest risk, do last or skip** | 5 |
| Indexer.cs | `DeleteDirectory` catch (~207) | D | **keep** (best-effort, runs in finally) | - |
| Indexer.cs | `Build` `try/finally` cleanup (~109-133) | D | **keep** | - |
| Json.cs | `Jsonl.Parse(string)` catch `JsonException` (~203) | B | single private `TryParse(..., out JsonDocument?)` edge | 6 |
| Json.cs | `Parse(ReadOnlyMemory<byte>)` catch (~241), hot | B | same edge; **no `Result` allocation per line** | 6 |
| Json.cs | `ParseNode` catch (~255) | B | same edge (keep the `valid` out flag: JSON `null` is valid) | 6 |
| Json.cs | `IntOr` `long.Parse` / `throw FormatException` (~44, 46-47) | A/C | see D1; the two `throw` arms are unreachable (`Truthy()` filters Null/False; enum fully covered) | 6 |
| Transcript.cs | `IsoFromMs` catch `ArgumentOutOfRangeException` (~175) | A | range check before conversion; **needs a boundary test** around year 9999 | 6 |
| Parsers.cs | `CodexReadRealtime` catch `IOException` (~161) | A | probe `sqlite_master` for `thread_realtime_items` first (D3 note: real I/O errors then fail the family) | 6 |
| Parsers.cs | state-titles catch (~287) | B | `Attempt.Io`, error ignored by design | 6 |
| Parsers.cs | `SubDirectories`, `Files` catches (~436, ~448) | B | one filtered adapter `Attempt.Io(() => [...]).GetValueOrDefault([])` replaces both copies | 6 |
| Parsers.CopilotVsCode.cs | `Replay` catch `ReplayException` (~312) + 8 throws (~329, 340, 356, 364, 367, 378, 380, 387) | A | `Result<T, ReplayError>` pipeline; delete `ReplayException` (section 6.4) | 6 |
| Parsers.CopilotVsCode.cs | `CopilotDesktopIds` catch (~41) | B | `SqliteReader.TryQuery` (materialized, small table) | 6 |
| Parsers.CopilotVsCode.cs | `WorkspaceName` catch (~157) | B | `Maybe<string>` + filtered adapter; keep public `string` return | 6 |
| SearchServer.cs | `Stop` catch `AggregateException` (~44) | B | `Result.Try`, `TapError(_ => {})` | 5 |
| SearchServer.cs | `Search` catch (~64) | B | private `Get(url, timeout)` returning `Result<string,string>`; keep `string? Search` public shape | 5 |
| SearchServer.cs | `IndexesDirectory` catch (~77) | B | same edge | 5 |
| Watcher.cs | `TryCycle` catch (~58) | D | **keep at the outermost edge**; may become `Result.Try(...).TapError(...)` | 5 |
| Watcher.cs | `Start` lambda catch (~148) returning `(TierState?, Exception?)` | B | `Task<Result<TierState,string>>`; tasks must never fault (`Task.WhenAll` depends on it) | 5 |
| Watcher.cs | `RestartServer` catch (~164) | B | `Result.Try(...).Match(...)` | 5 |
| Sqlite.cs | `throw IOException` open/prepare/step (~27, 33, 40) | B | keep throws; optional `Open` factory and `TryQuery` (lazy `Query` stays) | 6/7 |
| Sqlite.cs | `try/finally` statement finalize (~34-59) | D | **keep** | - |
| SelfTest.cs | `Check` throw (~260), temp-dir `finally` (~20-27) | C/D | **keep** | - |

### 5.2 Maybe/Bind/Map candidates (non-throwing; do only where it reads better and is off the hot path)

- Program.cs `Option` (returns null) -> `Maybe<string>`; `Exclude` ladder -> `Ensure/Bind/Tap`; `Find` unknown-option and `Show` arity checks -> `Ensure`.
- Corpus.cs line ~103 `previous.Families.TryGetValue(...) && known.Ok && fingerprint ==` -> `TryFind(...).Where(...)`.
- Corpus.cs/Indexer.cs `Store.Load<X>(...) ?? new X()` -> `GetValueOrDefault(() => new())` (cosmetic).
- Parsers.cs `CodexItemContent`, `CodexRolloutMessage`, `ClaudeMessage` if-return-null ladders -> `Maybe<Message>`.
- Parsers.CopilotVsCode.cs `UserText`, `OperationKind`, `Index`, `ChunkIndex`, `Keys` -> `Maybe`/`Result`.
- Finder.cs **cold paths only**: `WriteText`/`WriteJson` `byId.TryGetValue` ladders, `WorkItems` lookups, `Run` manifest null-coalescing.
- Compact.cs `FindSession` resolution ladder (exact -> only-non-subagent -> ambiguity) -> `Ensure` chain.

### 5.3 Do NOT convert

- `Store.Retry`, `Indexer.Build` finally, `Indexer.Publish` ordering, `Indexer.DeleteDirectory`, `Sqlite` handle code, `SelfTest.Check`.
- Lazy iterators (item 4 of the behavior contract).
- Hot loops: `Finder.ScanClaude/ScanCodex/ScanCopilot/ScanVsCode`, `Scan.Note`, `KeepSnippet`, `Window`, `Cluster`, `Json.Get/Str/...`
  (`Maybe<T>` is a struct but `TryFirst/Map/Bind` allocate lambdas and enumerators; `Result<T>` is a class).
- Pure total helpers (`Compact.Plain/Clip/Label/Time/SessionLine`, `Transcript.Normalize/SplitText/Render`, `Corpus.Fingerprint/CorpusHash/IsExcluded`).
- `Watcher` fields and loop (intentionally mutable state), `Capture` as a `Bind` pipeline (steps are `void`, shared mutable locals; no value).
- `Parsers.EnsureCanary` (startup failure should be fatal).

## 6. Design details

### 6.1 Shared pieces (WP0)

```csharp
internal sealed record CliError(string Message, int ExitCode);          // Program/Compact
internal static class Attempt                                              // filtered boundary adapters
{
    public static Result<T, string> Io<T>(Func<T> action) { /* catch IOException or UnauthorizedAccessException -> Failure($"{Type}: {Message}") */ }
    public static Result<T, string> Json<T>(Func<T> action) { /* catch JsonException */ }
}
```
Keep the error-text format `"{ExceptionType}: {Message}"` so persisted strings are unchanged.

### 6.2 Program.cs (WP4)

- `Option` stays pure-ish but returns `Maybe<string>`; ints parse with `int.TryParse(... CultureInfo.InvariantCulture)` into `Result<int,CliError>` (D2 message).
- Optional `CliOptions` record built by a pure parser from `ImmutableArray<string>` (replaces in-place `List<string>.Remove/RemoveRange`; call order matters today because `Skip(1)` assumes the verb is at index 0). Positionals are `ImmutableArray<string>`.
- `Exclude`:

```csharp
Result.Success<string, CliError>(id.Trim())
    .Ensure(i => i.Length >= 8, new CliError("Give at least 8 characters of the session ID.", 2))
    .Ensure(i => !Indexer.CanaryId.StartsWith(i, StringComparison.OrdinalIgnoreCase),
            new CliError("The canary session validates every index build and cannot be excluded.", 2))
    .Bind(i => exclude ? AddExclusion(root, i) : RemoveExclusion(root, i))   // returns Result<ImmutableArray<string>,CliError>; no printing inside
    .Tap(list => Store.Save(Corpus.ExclusionsPath(root), list))
    .Tap(_ => Console.WriteLine(exclude ? ExcludedHint : IncludedHint))
    .Match(_ => 0, e => Fail(e.Message, e.ExitCode));
```

  Printing of the "Excluded <id> (<client>) <title>" lines moves into a `Tap`. Order of output must stay the same.
- `Show`: validate ints with `Result.Combine`-style sequential `Bind`s (**evaluate `Number` in the existing order**: last, context, max, chars, then `--around`), then
  `Compact.Show(...)` returns `Result<string,CliError>` and `Tap(Compact.Write)`. Keep `Fail(message, 2)` for all failures.
- Leave `Run`, `Capture`, `Status`, `Fail` as they are.

### 6.3 Compact.cs (WP4)

`FindSession(root, id)` -> `Result<(string File, DocumentMeta Meta), CliError>` with the same ladder: exact match wins, then the only non-`:subagent:` candidate,
then 0 -> `No indexed session starts with '{id}'.`, >1 -> `Several sessions match; give more of the ID:\n` + first 10 lines. Messages byte-identical.
`Show` chains `FindSession -> ReadCorpus (Attempt.Io) -> Segments -> filter (around regex via Attempt wrapper incl. RegexMatchTimeoutException, 2 s) -> Render`.
`SelfTest.cs` calls `Compact.Show` directly (two places): update to `.Value` or a thin helper.

### 6.4 Replay (Parsers.CopilotVsCode.cs, WP6)

Write characterization tests first (current self-test only covers the happy path, `errors == 0`):
negative index, missing key, bad/missing `k`, array index out of range, `i` alone, empty key list, `Push` on non-array, kind -1 no-ops.
Assert exact `operations_failed` counts and final state.

Design: `enum ReplayError { BadPath, MissingKey, BadKeyType, BadIndex, NotArray, OutOfRange }` (only the count is consumed).
`Keys -> Result<ImmutableArray<JsonNode?>,ReplayError>`, `ReplayPosition`/`Index -> Maybe/Result`, `Child -> Result<JsonNode,ReplayError>`,
`Walk = keys.Aggregate(Result.Success(root), (acc,k) => acc.Bind(n => Child(n,k)))`,
`SetPath`/`Push -> UnitResult<ReplayError>` where **the mutating step is the last `Tap`, after all validation** (this preserves today's all-or-nothing per operation).
Loop: `Apply(op, state).Tap(s => state = s).TapError(_ => errors++)`; keep the `foreach` over the lazy `ReadNodes`.
Silent no-ops (kind -1, case 0 with non-object `v`, ops 1-3 while `state.Count == 0`) must return **Success**, not Failure.
`Take(operation)` mutates the operation object (not `state`); keep its position before `Push`/`SetPath`.

### 6.5 Corpus / Indexer / SearchServer / Watcher (WP5)

- `Store`: one private `ReadJson<T>(path, Func<Stream,T>)` edge; `Load`/`LoadNode` unwrap to null as today; `Save` and `Retry` unchanged.
- `ParseFamily`: `Result.Try` then `Match`; on failure `new FamilyReport { Ok = false, Error = "{Type}: {Message}" }`. `AddDocument` exceptions must stay **inside** the Try.
- `Indexer.RunLeann` returns `Result<LeannOutput,string>` (non-zero exit = failure); `Build` = `Stage -> RunLeann -> Ensure(canary) -> Publish` with `finally` kept for cleanup.
  The log file is written **before** the exit-code check (`Tap(WriteLog)` precedes `Ensure`).
  `Search` prints `LEANN search failed with exit code {exitCode}: {error}` to stderr and returns 1 at a single `Match`.
- `SearchServer`: private `Get(url, timeout)` = `Result.Try(GetStringAsync(...).GetAwaiter().GetResult())`; keep public `string? Search` and the "no server -> null -> caller falls back to a fresh process" contract.
- `Watcher`: `_running : Dictionary<string, Task<Result<TierState,string>>>`; `Collect` uses `Match(Publish, RecordFailure)`; error text stays `Type: Message`.
  `Log` can throw inside catch handlers today (latent): make it non-throwing, but do not otherwise change the loop.

### 6.6 Records and immutable collections

Serialization rules (System.Text.Json, `SnakeCaseLower`):
- Positional records bind constructor parameters through the naming policy, but a **missing JSON field passes `default`** (null string/dictionary), which breaks `.Count`/`.Keys`.
  Therefore use **init-only records with property initializers** for deserialized types: `DocumentMeta`, `FamilyReport`, `Manifest`, `TierState`.
- `Store.Load<T>` is constrained `where T : class`: no `record struct` for anything loaded through it.
- `ImmutableArray<T>` serializes as a JSON array; `default(ImmutableArray<T>)` throws on `foreach`/LINQ/serialization.
  Never expose a deserialized `ImmutableArray` property without an initializer (`= []`). `Store.Load<ImmutableArray<string>>(...) ?? []` does **not** compile (struct); load a `List<string>` and convert.
- `.Count` becomes `.Length` (about 15 sites, mostly `SelfTest.cs`); `foreach`/indexers/`[^1]`/`Any`/`SequenceEqual` work; `Aggregate` and `Select` work without boxing.
  Passing to `IReadOnlyList<T>`/`IEnumerable<T>` boxes once (harmless off hot paths).
- Collection expressions (`[..]`, `["a","b"]`) target `ImmutableArray<T>`. `new[] { ... }` does **not** convert implicitly; use `[..]` or `.ToImmutableArray()`.
- Do not use `ImmutableDictionary` where enumeration order matters; do not convert accumulators (`stats`, `sessions`, `assistants`, dedupe maps) - freeze only at the `FamilyResult`/`Document` boundary.
- Method-group trap: `.SelectMany(DatabaseFiles)` stops compiling (CS0407) if `DatabaseFiles` returns `ImmutableArray`; keep `string[]` or use a lambda.

Type verdicts:

| Type (file) | Verdict | Shape |
|---|---|---|
| `Message` (Transcript.cs) | Ready | `sealed record Message(string Id, string Role, string Text, long Sequence, string Timestamp)`; same ctor order, ~12 call sites unchanged |
| `DocumentMeta` (Corpus.cs) | Ready | `sealed record`, keep `init` properties and `new() { ... }` sites |
| `FamilyReport` (Corpus.cs) | Ready | `sealed record`, init properties with initializers (`Stats = []`, `Error = ""`) |
| `Manifest` (Corpus.cs) | Ready | `sealed record`, `init` setters; **keep `Dictionary` fields** (`?? []` sites in Compact/Finder/Program rely on it) |
| `CaptureReport` (Corpus.cs) | Ready | `sealed record CaptureReport(bool Changed, Manifest Manifest, double DurationSeconds, ImmutableArray<string> Reparsed)`; never deserialized |
| `StagedBuild` (Indexer.cs) | Ready | `sealed record`; never serialized |
| `TierState` (Indexer.cs) | With changes | init-only record with initializers; nothing mutates it; loaded via `Store.Load` with `?? new TierState()` fallback |
| `Family` (Parsers.cs) | Ready | `sealed record Family(string Name, Func<ImmutableArray<string>> Sources, Func<FamilyResult> Parse)`; delegates compare by reference like today |
| `Document` (Transcript.cs) | With changes | `sealed record Document(string Family, string Client, string SessionId, string SourceRef, ImmutableArray<Message> Messages) { public string Title { get; init; } = ""; }`; 8 construction sites |
| `FamilyResult` (Transcript.cs) | Ready (with `Document`) | `sealed record FamilyResult(ImmutableArray<Document> Documents, Dictionary<string,long> Stats)`; 6 construction sites |
| `Snippet`, `Compact.Segment` | Already records | keep |
| `Found`, `Scan` (Finder.cs) | Keep class | mutable accumulators; optional freeze of only the displayed entries |
| `JsonlStats` (Json.cs) | Keep class | mutated through iterator callees; iterators cannot take `ref` |
| `AssistantMessage`, `ReplayException`, `SqliteReader`, `Watcher`, `Store`, static classes | Keep | resource/accumulator/exception types |

Collection changes (recommended):

| Where | Change |
|---|---|
| Terms flow: `Program.Find` -> `Finder.Run`, `WorkItems`, `WriteText/WriteJson`, `Window`, `Cluster`, `Scan` ctor, `ClaudeExcerpt` | `IReadOnlyList<string>` -> `ImmutableArray<string>`; guard `IsDefaultOrEmpty` in `Run` (`find --top 3` allows empty terms); replace `terms.Count(pred)` in `Consider` with a loop; `.Count` -> `.Length` at ~103, 390, 401 |
| `Scan._forms`, `Scan.Forms` | `ImmutableArray<ImmutableArray<string>>`, `ImmutableArray<string>` |
| `Finder` tuple key `(string Client, string SessionId)` (about 8 places) | `readonly record struct SessionKey(string Client, string SessionId)` (it is a dictionary key; value equality preserved) |
| `Compact.Segments` | `ImmutableArray<Segment>` via builder; `.Count` -> `.Length` in `Show` and `SelfTest` |
| `Indexer.Tiers`, `Filter(tiers)` | `ImmutableArray<string>`; `Filter` returns `ImmutableArray<JsonObject>` (shallow only; `JsonObject` stays mutable) |
| `Indexer.Search` `recent`/`current` | `IReadOnlySet<string>` stays the parameter type; build with `.ToFrozenSet()` |
| `Parsers.All`, `Family.Sources`, `CodexSources`, `CopilotSources`, `SyntheticFiles`, `ChatFiles`, `Watcher._families`, `Corpus.Capture(families)` | `ImmutableArray<Family>`/`ImmutableArray<string>`; change as one unit (cross-file) |
| `Corpus.LoadExclusions` | stays `List<string>` (Program mutates); `SearchExclusions` returns `ImmutableArray<string>`; `IsExcluded` takes `ImmutableArray<string>` or `ReadOnlySpan<string>` |
| `CaptureReport.Reparsed` | `ImmutableArray<string>` |
| `Transcript.SplitText` | `ImmutableArray<string>` |
| `Json.ByteOrderMark` | `static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];` (not ImmutableArray) |
| Replay key paths | `ImmutableArray<JsonNode?>` (shallow immutability; consider passing the `JsonArray` to avoid a copy per op) |
| Tuples `Query` (4 values) and `RunLeann` (3 values) | optional named `readonly record struct` |
| Long parameter lists | optional `FindRequest` / `ShowRequest` records for `Finder.Run` and `Compact.Show` (two `Compact.Show` calls in `SelfTest.cs` pass positional args) |

Keep as-is: `stats` dictionaries, `sessions`/`assistants`/dedupe maps, `Manifest.Documents/Families`, `Find`'s `work` list, `Compact.Keys`, `Compact.Ascii` (optional `FrozenDictionary`), `params string[]` helpers
(prefer `params ReadOnlySpan<string>` for `Stats`/`SessionLine`; `Jsonl.Read` tokens cannot be a span because it is an iterator), `SqliteReader.Query` rows
(optional `ImmutableCollectionsMarshal.AsImmutableArray(row)`; `GetValueOrDefault(..) ?? [null,...]` at `CopilotVsCode ~117` then stops compiling).

## 7. Verification

### 7.1 CLI behavior probes (run before and after each WP that touches Program/Compact; outputs and exit codes must match unless a decision says otherwise)

Use a throwaway root or the installed one read-only, e.g.
`dotnet run --project SessionMemory\SessionMemory.csproj --no-build -- --root <dir> <command>`.

| Command | Expected |
|---|---|
| `exclude abc` | stderr `Give at least 8 characters of the session ID.`, exit 2 |
| `exclude <canary prefix>` | stderr `The canary session validates every index build and cannot be excluded.`, exit 2 |
| `exclude <no-such-id>` | stderr `No indexed session starts with '<id>'.`, exit 1 |
| `include <not-excluded-id>` | stderr `'<id>' is not excluded.`, exit 1 |
| `show a` / `show <ambiguous prefix>` | stderr `Several sessions match; give more of the ID:` + up to 10 lines, exit 2 |
| `show zzzzzzzz` | stderr `No indexed session starts with 'zzzzzzzz'.`, exit 2 |
| `find` with an unknown `--x` | stderr `Unknown option '--x'.` + usage, exit 2 |
| `search q --top-k x` | usage + number error (text changes per D2), exit 2 |
| `find <term> --include-current` vs default | current session listed vs not (see behavior contract 8) |

### 7.2 Automated

1. **Build** with Rider MCP (`rider-build_solution_start`, poll `rider-build_solution_state`): must report success with 0 problems.
2. **Self-test**: `dotnet run --project SessionMemory\SessionMemory.csproj --no-build -- --root <temp dir> self-test` must print `{"ok": true}` (it uses its own temp dir).
3. **Rider inspections**: `rider-lint_files` on every changed file. Actionable findings must be **0**. Allocation markers from the HeapView plugin
   (descriptions starting `Object allocation`, `Possible object allocation`, `Closure allocation`, `Delegate allocation`, `Boxing allocation`) are informational and are
   not fixed; the baseline had about 568. Bare-method-name warnings come from the CognitiveComplexity plugin (method over threshold).
4. **JSON round trip** (WP1/WP3/WP5): copy `%LOCALAPPDATA%\LeannSessionMemory\state\manifest.json` to a temp dir, `Store.Load<Manifest>` then `Store.Save`, and compare to the original
   (same property names, same key order, same values). Add this as a self-test check using a small synthetic manifest that includes a family with `ok:false` + `error`.
5. **New self-test checks to add** (each WP lists its own): `exclude` validation rail (too short, canary, unknown id, success), `FindSession` (0/1/many/exact/subagent),
   `IsoFromMs` boundary (`253402300799999.6` and neighbours, negative, NaN, infinity), replay failures (6.4), `ParseFamily` failure text format,
   `SearchServer` unreachable returns null, terms flow with empty terms, `Document`/`FamilyResult` construction.
6. **Hygiene after any edit**: no `\*{4,}` sequence in any `.cs`; every `.cs` still has a BOM; `git diff --stat` shows only intended files.

### 7.3 Deploying to the installed tool (only when the user asks)

Do **not** run `Install-SessionMemory.ps1` (it reinstalls LEANN and rewrites global instruction files). The program-only steps used previously:

1. Back up `%LOCALAPPDATA%\LeannSessionMemory\bin\*` to `backups\bin-<yyyyMMdd-HHmmss>`.
2. `Disable-ScheduledTask LeannSessionMemoryRefresh`, `Stop-ScheduledTask`, stop `SessionMemory.exe`/`leann-dotnet.exe` that run from the install path
   (match by `ExecutablePath`, never by name alone), and call `http://localhost:57392/shutdown`.
3. `dotnet publish SessionMemory\SessionMemory.csproj --configuration Release --output %LOCALAPPDATA%\LeannSessionMemory\bin`.
4. `Enable-ScheduledTask`, `Start-ScheduledTask`; confirm the watcher process is running and `find`/`search` work through `bin\SessionMemory.exe` and `bin\Search-SessionMemory.ps1`.
5. Rollback = copy the backup folder over `bin\` after stopping the watcher.

## 8. Work packages (dependency order) and the demo track

| WP | Title | Depends on | Effort |
|---|---|---|---|
| 0 | Foundations: package, `CliError`, `Attempt` adapters | - | S |
| 1 | Records, phase A (non-breaking): `Message`, `DocumentMeta`, `FamilyReport`, `Manifest`, `CaptureReport`, `StagedBuild`, `TierState`, `Family` | - | S/M |
| 2 | ImmutableArray, phase A: terms flow, `_forms`, `SessionKey`, `Segments`, `Tiers`/`Filter`/`FrozenSet`, `ByteOrderMark`, `SplitText`, exclusions, `Reparsed` | 1 (for `CaptureReport`) | M |
| 3 | `Document` + `FamilyResult` + family lists (`All`, `Sources`, `ChatFiles`, `_families`, `Capture`) | 1, 2 | M |
| 4 | **Railway, CLI**: `Program` (`TryParse`, `Exclude`, `Show`, optional `CliOptions`), `Compact.FindSession`/`Show` | 0 | M |
| 5 | Railway, infrastructure: `Store`/`ParseFamily`, `Indexer` (`Search`, `RunLeann`, `Build`/`Validate`), `SearchServer`, `Watcher` | 0 (and 1 for `TierState`) | M/L |
| 6 | Railway, parsers: replay, optional-table probes, `WorkspaceName`, `IsoFromMs`, Json `TryParse`, `IntOr` | 0; decisions D1, D3 | M/L |
| 7 | Optional polish: request records, tuple records, `Found` freeze, `Sqlite.Open`/`TryQuery`, `Maybe` ladders | any | S each |

**Demo track (fastest path to show the functional style): WP0 + WP4.** Show `exclude abc`, `exclude <canary>`, `exclude <real id>`, `show a` and `show zzzzzzzz`;
each failure leaves the rail at a different `Ensure`/`Bind` and the remaining steps are skipped. No other WP is required.

Suggested order for the full effort: 0 -> 4 -> 1 -> 2 -> 3 -> 5 -> 6 -> 7. WP5 `Indexer.Publish` rollback conversion is optional and last.

### Per-WP acceptance (all WPs also require section 7.2 items 1-3 and 6)

- **WP0**: package restores; `Attempt.Io/Json` unit-checked in `SelfTest` for filtered vs unfiltered exception behavior (an `ArgumentException` must propagate).
- **WP1**: JSON round trip (7.2.4) passes; existing `SelfTest` initializers compile unchanged; no call-site edits for `Message`.
- **WP2**: `find` with 0, 1, many terms and `--partial`/`--session`/`--json` produce identical output to baseline on the real index (compare to a saved baseline run); no per-line allocation added in `Scan`.
- **WP3**: all 14 construction sites updated; `Corpus.Capture` on the synthetic family yields the same manifest hash as before.
- **WP4**: probe table 7.1 matches; `show` on a missing corpus file now returns exit 2 with a message instead of crashing.
- **WP5**: forced failures behave as before: missing LEANN path -> watcher backoff + same persisted text; `SearchServer` down -> `Indexer.Search` falls back to a fresh process.
- **WP6**: replay characterization tests pass before and after with identical error counts; corpus hash for real VS Code and Copilot data is unchanged (compare `corpus_hash` from a `capture` run before/after on a copy of the data).
- **WP7**: each item independently verified.

## 9. Parallelization and file ownership (for fleet runs)

Give each worker exclusive ownership; shared files go to the integrator.

| Worker | Files |
|---|---|
| A (CLI) | `Program.cs`, `Compact.cs`, `SelfTest.cs` (CLI checks only) |
| B (Corpus/Transcript) | `Corpus.cs`, `Transcript.cs` |
| C (Indexer/Watcher/Server) | `Indexer.cs`, `Watcher.cs`, `SearchServer.cs` |
| D (Finder) | `Finder.cs` |
| E (Parsers) | `Parsers.cs`, `Parsers.CopilotVsCode.cs` |
| F (Json/Sqlite) | `Json.cs`, `Sqlite.cs` |
| Integrator | `SessionMemory.csproj`, shared records (`CliError`, `Attempt`), cross-file signature changes, build, self-test, lint, diff review |

Cross-file signature changes (e.g. `Parsers.All` -> `ImmutableArray<Family>`, `Document.Messages`) must be done in one agreed step by the integrator or in lockstep by the owners.
Workers must not build; the integrator runs the Rider build, self-test and lint after merging.

## 10. Risks and traps (summary)

- `Result.Try` is broader than the current filtered catches (D5).
- `default(ImmutableArray<T>)` is uninitialized; `.Count` vs `.Length`; `new[]` literals; `Store.Load<ImmutableArray<T>> ?? []` does not compile; CS0407 method-group break.
- Positional-record deserialization passes `default` for missing fields (use init-only with initializers for serialized types).
- Manifest key order and persisted error text.
- Lazy iterators must stay lazy; hot loops must not gain allocations.
- `Publish` rollback order; `Build` cleanup in `finally`; watcher tasks must never fault.
- Latent issues found (not part of this spec, fix separately if wanted): `Store.Save`/`WriteChangedDocuments` can leak a `.tmp` file when the final move fails;
  `Watcher.Log` can throw inside catch handlers; a throw from `Store.Save` in `Watcher.Collect` leaves the build unrecorded so it is retried immediately;
  `CopilotEventFiles` is enumerated twice in `CopilotSources` (walks the file system twice); `Path.GetDirectoryName(path)!` at `Finder ~454` would throw on a root path.
