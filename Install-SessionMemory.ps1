param(
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:LOCALAPPDATA 'LeannSessionMemory'
$Bin = Join-Path $Root 'bin'
$State = Join-Path $Root 'state'
$Exe = Join-Path $Bin 'SessionMemory.exe'
$InstallState = Join-Path $State 'install.json'
$TaskName = 'LeannSessionMemoryRefresh'
# A private LEANN .NET, so the global tool and its running code-search servers are never touched.
# 2.10 or later is required: it ranks the recent and archive indexes together in one search.
$LeannVersion = '2.10.1'
$LeannDir = Join-Path $Root 'leann'
$ExistingInstall = if (Test-Path $InstallState) { Get-Content $InstallState -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$Section = '<!-- leann-session-memory:start -->.*?<!-- leann-session-memory:end -->'

# Processes started from this install: the watcher, or a search server or index build it launched.
function Stop-Owned([string]$Name) {
    Get-CimInstance Win32_Process -Filter "Name = '$Name'" |
        Where-Object { $_.ExecutablePath -eq $Exe -or "$($_.ExecutablePath)".StartsWith("$LeannDir\", 'OrdinalIgnoreCase') } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $_.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
}

function Stop-Watcher {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        # Disabled first, so the one-minute trigger can't restart the watcher while its files are replaced.
        Disable-ScheduledTask -TaskName $TaskName | Out-Null
        Stop-ScheduledTask -TaskName $TaskName
    }
    Stop-Owned 'SessionMemory.exe'
    Stop-Owned 'leann-dotnet.exe'
}

function Write-InstallState($Install) {
    $Temporary = "$InstallState.$([guid]::NewGuid().ToString('N')).tmp"
    $Replaced = "$Temporary.bak"
    try {
        $Install | ConvertTo-Json -Depth 5 | Set-Content $Temporary -Encoding utf8
        if (Test-Path $InstallState) { [IO.File]::Replace($Temporary, $InstallState, $Replaced) }
        else { [IO.File]::Move($Temporary, $InstallState) }
    }
    finally {
        if (Test-Path $Temporary) { Remove-Item $Temporary -Force -ErrorAction SilentlyContinue }
        if (Test-Path $Replaced) { Remove-Item $Replaced -Force -ErrorAction SilentlyContinue }
    }
}

$Targets = @(
    (Join-Path $HOME '.codex\AGENTS.md'),
    (Join-Path $HOME '.claude\CLAUDE.md'),
    (Join-Path $HOME '.copilot\copilot-instructions.md')
)

function Remove-InstallDirectory([string]$Path) {
    $Full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $Allowed = @([IO.Path]::GetFullPath($Bin).TrimEnd('\'), [IO.Path]::GetFullPath($LeannDir).TrimEnd('\'))
    if ($Full -notin $Allowed) { throw "Unsafe recovery directory: $Path" }
    if (Test-Path -LiteralPath $Full) { Remove-Item -LiteralPath $Full -Recurse -Force }
}

function Assert-BinaryBackup([string]$Path) {
    $BackupBase = [IO.Path]::GetFullPath((Join-Path $Root 'backups')).TrimEnd('\') + '\'
    $Full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $Full.StartsWith($BackupBase, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $Full -Leaf) -ne 'binary') { throw 'Unsafe recovery backup path' }
}

function Remove-BinaryBackup([string]$Path) {
    Assert-BinaryBackup $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue }
}

function Restore-Install($Recovery) {
    $Backup = [IO.Path]::GetFullPath([string]$Recovery.binaryBackup).TrimEnd('\')
    Assert-BinaryBackup $Backup
    foreach ($Item in @(@{ name = 'bin'; exists = $Recovery.binExisted }, @{ name = 'leann'; exists = $Recovery.leannExisted })) {
        if ($Item.exists -and -not (Test-Path -LiteralPath (Join-Path $Backup $Item.name) -PathType Container)) { throw "Missing recovery backup: $($Item.name)" }
    }
    foreach ($Snapshot in $Recovery.instructionFiles) {
        if ($Snapshot.path -notin $Targets) { throw "Unsafe recovery instruction path: $($Snapshot.path)" }
        $ExistsNow = Test-Path -LiteralPath $Snapshot.path
        $CurrentBytes = if ($ExistsNow) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Snapshot.path)) } else { $null }
        $OriginalMatches = if ($Snapshot.exists) { $ExistsNow -and $CurrentBytes -ceq $Snapshot.bytes } else { -not $ExistsNow }
        if (-not $OriginalMatches -and (-not $ExistsNow -or $null -eq $Snapshot.postBytes -or $CurrentBytes -cne $Snapshot.postBytes)) {
            throw "Instruction file changed since the interrupted install: $($Snapshot.path). Recovery state and backups were retained."
        }
    }
    Stop-Watcher
    foreach ($Item in @(@{ name = 'bin'; path = $Bin; exists = $Recovery.binExisted }, @{ name = 'leann'; path = $LeannDir; exists = $Recovery.leannExisted })) {
        Remove-InstallDirectory $Item.path
        if ($Item.exists) { Copy-Item -LiteralPath (Join-Path $Backup $Item.name) -Destination $Item.path -Recurse -Force }
    }
    foreach ($Snapshot in $Recovery.instructionFiles) {
        if ($Snapshot.exists) {
            New-Item -ItemType Directory -Force -Path (Split-Path $Snapshot.path) | Out-Null
            [IO.File]::WriteAllBytes($Snapshot.path, [Convert]::FromBase64String([string]$Snapshot.bytes))
        }
        elseif (Test-Path -LiteralPath $Snapshot.path) { Remove-Item -LiteralPath $Snapshot.path -Force }
    }
    if ($Recovery.taskXml) {
        Register-ScheduledTask -TaskName $TaskName -Xml $Recovery.taskXml -Force | Out-Null
        if ($Recovery.taskEnabled) { Enable-ScheduledTask -TaskName $TaskName | Out-Null }
        else { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
        if ($Recovery.taskRunning) { Start-ScheduledTask -TaskName $TaskName }
    }
    elseif (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    if ($null -ne $Recovery.previousState) {
        $Temporary = "$InstallState.$([guid]::NewGuid().ToString('N')).tmp"
        $Replaced = "$Temporary.bak"
        try {
            [IO.File]::WriteAllBytes($Temporary, [Convert]::FromBase64String([string]$Recovery.previousState))
            [IO.File]::Replace($Temporary, $InstallState, $Replaced)
        }
        finally {
            if (Test-Path $Temporary) { Remove-Item $Temporary -Force -ErrorAction SilentlyContinue }
            if (Test-Path $Replaced) { Remove-Item $Replaced -Force -ErrorAction SilentlyContinue }
        }
    }
    else { Remove-Item $InstallState -Force }
    Remove-BinaryBackup $Backup
}

if ($ExistingInstall.recovery) {
    Restore-Install $ExistingInstall.recovery
    if ($Rollback) {
        Write-Output 'Interrupted installation recovered to its previous state.'
        exit 0
    }
    $ExistingInstall = if (Test-Path $InstallState) { Get-Content $InstallState -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
}

if ($Rollback) {
    Stop-Watcher
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    # Only the memory section is removed, so edits made since the install are kept.
    foreach ($Entry in $ExistingInstall.instructionFiles) {
        if (-not (Test-Path $Entry.path)) { continue }
        $Remaining = [regex]::Replace([string](Get-Content $Entry.path -Raw -Encoding UTF8), "(?s)$Section", '')
        if (-not [string]::IsNullOrWhiteSpace($Remaining) -or $Entry.existed) {
            Set-Content -Path $Entry.path -Value $Remaining -Encoding utf8 -NoNewline
        }
        else {
            Remove-Item $Entry.path -Force
        }
    }
    # A later install then backs up the files as they are at that point.
    if (Test-Path $InstallState) { Remove-Item $InstallState -Force }
    Write-Output 'Session-memory instructions and scheduled task removed. Corpus and indexes were preserved.'
    exit 0
}

if (-not (& dotnet --list-sdks 2>$null | Select-String '^10\.')) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$PreviousTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
$PreviousTaskXml = if ($PreviousTask) { Export-ScheduledTask -TaskName $TaskName } else { $null }
$PreviousStateBytes = if (Test-Path $InstallState) { ,([IO.File]::ReadAllBytes($InstallState)) } else { $null }
$BackupRoot = Join-Path $Root ('backups\' + [guid]::NewGuid().ToString('N'))
$BinaryBackup = Join-Path $BackupRoot 'binary'
Assert-BinaryBackup $BinaryBackup
$BinExisted = Test-Path -LiteralPath $Bin -PathType Container
$LeannExisted = Test-Path -LiteralPath $LeannDir -PathType Container
$Search = Join-Path $Bin 'Search-SessionMemory.ps1'
$Block = @"
<!-- leann-session-memory:start -->
## Local LEANN session memory

Before repeating an investigation, or when prior decisions, failed attempts, corrections, or unfinished work may matter, run:

``````powershell
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "$Search" -Query "<specific question>"
``````

Search combines SQLite FTS5 Porter BM25 over overlapping windows of logical neighboring messages with LEANN semantic windows, fused with RRF at 60. It returns up to 8 windows by default (up to 50 with ``-TopK``) within a 12,000-character actual-text budget. Results cite original messages with role and timestamp; one message may produce multiple windows. Search runs locally without an LLM.

For a multi-term lexical match, the topic and another positive query term must share one source passage. Semantic-ranked windows matched to original messages may also qualify when the topic appears anywhere in the message, or when no query words overlap, as with a paraphrase. Generic qualifier-only matches are dropped.

For latest or current-status questions, add ``-Latest``. Message dates inform ranking alongside relevance, but results do not guarantee completeness or establish current state. Use ``-Session <id>`` to narrow a search to one session (at least 8 characters; it conflicts with ``-Current``). ``-ExcludeTopic <literal phrase>`` filters only that literal phrase when requested; ordinary question wording does not imply negation, so comparisons can retain both sides.

For latest/history navigation, run ``timeline <literal-term> [<term> ...]`` with actual subject terms. It scans visible cached originals using literal OR matching, not relevance ranking; a term match does not prove the session did the work. It reports UTC dates, undated status, result counts, truncation and stale-cursor markers. ``--top`` defaults to 12 (max 50); ``--cursor`` is opaque; ``--include-current`` and ``--json`` are available. Matching case-folds invariantly but does not normalize Unicode composition, so code-point sequences must match.

Use hierarchical source reading before answering: run a plain question or timeline to identify candidate sessions, then search for the specific fact within each relevant session (``-Session <id>`` in the wrapper). Initial windows are leads, not a full answer. Follow a navigation result's singular ``read_hint`` command as printed; it may include ``--offset``. Search results' ``read_context`` starts at the original message beginning and has no offset. Read a main citation with ``& "$Exe" show <id> --message <positive original message number> --offset <n> --context 1 --chars 6000``. The joined original message preserves blank lines and includes its UTC timestamp. Offsets are nonnegative UTF-16 character units and work only with ``--message``. Exact-read output shows ``[offset N; next offset N/end; total N UTF-16 units]``; the displayed offset may move back to a word or surrogate boundary, and an offset past the message errors. Continue with the displayed next offset if clipped. ``--message`` conflicts with ``--around``; an exact message miss exits with 2. ``--context`` accepts 0 to 5 and ``--chars`` accepts 200 to 20,000. If output has an omission marker, continue reading, refine the search, or say the evidence is unknown; never assume clipped text is complete. ``--around`` and ``--last`` remain available.

To locate an indirect update, run ``related <source-id> --message <number> --top <n>`` on a specific cited source message. It follows one hop of explicit HTTP(S) URIs or full known session IDs and returns referring/target citations plus visible known-session navigation. URI path, query and fragment must match exactly; a focused comment URI is not a base-page alias. It does not infer branches or topics or fetch the web. Treat a shared reference as a navigation hint, not proof of shared work; do not open every related session wholesale.

If a lookup or summary names an earlier work session, search that session or title and attribute the work to the source session, not the reporter. Distinguish reporting from doing. For date-bound questions, compare relevant message timestamps in UTC, not session last activity; do not use a future event to answer a past-state question or assert global latest without adequate coverage. Answer only the material facts requested. A proposal, intention or request does not prove the action was completed.

For searches tied to tracked work, include the ticket ID and full PR URL when relevant; search does not follow issue, PR or branch links automatically. If results are insufficient, refine the query or run another search. Cite status claims with source session, message and date. Distinguish proposals, patches, merges, publication and verification, and say when the evidence is insufficient.

For verification, keep the UTF-8 JSON schema ``{ "claims": [{ "claim": "...", "session_id": "...", "message_number": 1, "quote": "..." }] }`` unchanged. Submit at most 12 claims in a file no larger than 64 KiB with ``& "$Exe" verify <claims-json-file>``. It checks quote presence and returns context within 12,000 source characters; a matching quote does not prove meaning or truth. Review the source yourself.

Use one plain natural-language question describing what happened (for example, "What hardware specs did I request for my new Linux VM?"). If results are insufficient, rephrase or run a focused follow-up search. Use ``-Latest`` when recency matters.

Search results show source message dates, roles and citations. To read more, run ``& "$Exe" show <session id> --last 15`` or ``--around "<regex>"``; do not open transcript files directly.

Use ``find`` only for exact names, IDs, numbers, paths or error text that may sit in file edits, commands or page updates rather than in the conversation: run ``& "$Exe" find <term> [<term> ...]``. It lists sessions where every term appears as a whole word, newest first, with one excerpt each; add ``--partial`` to match inside words, or ``--session <session id>`` to see every matching excerpt in one session. It costs more than search, so skip it for questions about status, decisions or reasons.

Use chronology and explicit corrections. Compare source-message timestamps, not session last activity or file modification time. A newer message may still be a reposted summary; inspect direct source work before treating it as current. Cite status claims with source session, message and date. Searches leave out your current conversation by default. After context compaction, use ``find ... --current`` or ``show --current --around "<regex>"`` to recover exact details. Keep context bounded.
<!-- leann-session-memory:end -->
"@
$SavedFiles = @()
$Snapshots = @()
$Updates = @()
foreach ($Target in $Targets) {
    $Previous = @($ExistingInstall.instructionFiles | Where-Object path -eq $Target)[0]
    $Existed = if ($Previous) { [bool]$Previous.existed } else { Test-Path $Target }
    $Backup = if ($Previous) { [string]$Previous.backup } else { Join-Path $BackupRoot ((Split-Path $Target -Leaf) + '-' + ([guid]::NewGuid().ToString('N')) + '.bak') }
    if (-not $Previous -and $Existed) {
        New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
        Copy-Item $Target $Backup -Force
    }
    # Windows PowerShell reads BOM-less files as ANSI unless told otherwise.
    $Current = if (Test-Path $Target) { [string](Get-Content $Target -Raw -Encoding UTF8) } else { '' }
    $Pattern = "(?s)$Section"
    $Match = [regex]::Match($Current, $Pattern)
    $Updated = if ($Match.Success) {
        $Current.Substring(0, $Match.Index) + $Block.Trim() + [regex]::Replace($Current.Substring($Match.Index + $Match.Length), $Pattern, '')
    }
    elseif ([string]::IsNullOrWhiteSpace($Current)) {
        $Block.Trim() + [Environment]::NewLine
    }
    else {
        $Current.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine + $Block.Trim() + [Environment]::NewLine
    }
    $Encoding = New-Object System.Text.UTF8Encoding -ArgumentList ($PSVersionTable.PSVersion.Major -le 5)
    $PostBytes = [Convert]::ToBase64String($Encoding.GetPreamble() + $Encoding.GetBytes($Updated))
    $Updates += [pscustomobject]@{ path = $Target; value = $Updated }
    $Snapshots += [pscustomobject]@{ path = $Target; exists = (Test-Path $Target); bytes = $(if (Test-Path $Target) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Target)) } else { $null }); postBytes = $PostBytes }
    $SavedFiles += [pscustomobject]@{ path = $Target; existed = $Existed; backup = $Backup }
}
New-Item -ItemType Directory -Force -Path $BinaryBackup, $State | Out-Null
if ($BinExisted) { Copy-Item -LiteralPath $Bin -Destination (Join-Path $BinaryBackup 'bin') -Recurse -Force }
if ($LeannExisted) { Copy-Item -LiteralPath $LeannDir -Destination (Join-Path $BinaryBackup 'leann') -Recurse -Force }
$Install = [pscustomobject]@{
    installedAt = (Get-Date).ToUniversalTime().ToString('o')
    root = $Root
    executable = $Exe
    leann = Join-Path $LeannDir 'leann-dotnet.exe'
    leannVersion = $LeannVersion
    taskName = $TaskName
    instructionFiles = $SavedFiles
    recovery = [pscustomobject]@{
        previousState = $(if ($null -ne $PreviousStateBytes) { [Convert]::ToBase64String($PreviousStateBytes) } else { $null })
        instructionFiles = $Snapshots
        binaryBackup = $BinaryBackup
        binExisted = [bool]$BinExisted
        leannExisted = [bool]$LeannExisted
        taskXml = $PreviousTaskXml
        taskEnabled = [bool]($PreviousTask -and $PreviousTask.State -ne 'Disabled')
        taskRunning = [bool]($PreviousTask -and $PreviousTask.State -eq 'Running')
    }
}
Write-InstallState $Install
$Recovery = $Install.recovery
try {
    Stop-Watcher
    New-Item -ItemType Directory -Force -Path $Bin | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'SessionMemory.csproj') --configuration Release --output $Bin --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $LeannVerb = if (Test-Path (Join-Path $LeannDir 'leann-dotnet.exe')) { 'update' } else { 'install' }
    & dotnet tool $LeannVerb leann-dotnet --version $LeannVersion --tool-path $LeannDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Installing LEANN .NET $LeannVersion failed" }
    if (-not (Test-Path (Join-Path $HOME '.leann\models\facebook-contriever\model.onnx'))) {
        & (Join-Path $LeannDir 'leann-dotnet.exe') --setup --model facebook/contriever
        if ($LASTEXITCODE -ne 0) { throw 'Downloading the facebook/contriever model failed' }
    }
    Copy-Item (Join-Path $PSScriptRoot 'Search-SessionMemory.ps1'), $PSCommandPath -Destination $Bin -Force
    & $Exe run --once --progress
    if ($LASTEXITCODE -ne 0) { throw 'Initial session indexing failed' }

foreach ($Snapshot in $Snapshots) {
    $ExistsNow = Test-Path -LiteralPath $Snapshot.path
    $CurrentBytes = if ($ExistsNow) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Snapshot.path)) } else { $null }
    if ($ExistsNow -ne $Snapshot.exists -or ($ExistsNow -and $CurrentBytes -cne $Snapshot.bytes)) {
        throw "Instruction file changed while setup was running: $($Snapshot.path). Recovery state and backups were retained."
    }
}
foreach ($Update in $Updates) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Update.path) | Out-Null
    Set-Content -Path $Update.path -Value $Update.value -Encoding utf8 -NoNewline
}

# The one-minute trigger starts the watcher after logon, a crash, or a reboot; IgnoreNew keeps a single instance.
# conhost --headless gives the console app no visible window.
$Action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\conhost.exe') -Argument "--headless `"$Exe`" run"
$Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)
$Settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Settings $Settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

$Recovery = $Install.recovery
$Install.PSObject.Properties.Remove('recovery')
Write-InstallState $Install
Remove-BinaryBackup $BinaryBackup
$Install | ConvertTo-Json -Depth 5
}
catch {
    $Failure = $_
    try { Restore-Install $Recovery }
    catch { throw "Install failed: $Failure. Recovery incomplete; retry with $InstallState retained. $($_.Exception.Message)" }
    throw $Failure
}
