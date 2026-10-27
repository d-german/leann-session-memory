param([int]$TimeoutSeconds = 600)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Run this test only on a disposable GitHub-hosted Windows runner.'
}
$installerPath = Join-Path $PSScriptRoot '..\Install-SessionMemory.ps1'
$installRoot = Join-Path $env:LOCALAPPDATA 'LeannSessionMemory'
$installState = Join-Path $installRoot 'state\install.json'
$taskName = 'LeannSessionMemoryRefresh'
$memoryExe = Join-Path $installRoot 'bin\SessionMemory.exe'
$profileRoot = [Environment]::GetFolderPath('UserProfile')
$targets = @(
    (Join-Path $profileRoot '.codex\AGENTS.md'),
    (Join-Path $profileRoot '.claude\CLAUDE.md'),
    (Join-Path $profileRoot '.copilot\copilot-instructions.md')
)
if ((Test-Path $installRoot) -or (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
    throw 'This test requires a fresh profile without an existing SessionMemory installation.'
}
foreach ($target in $targets) {
    if (Test-Path $target) { throw "Fresh-profile instruction file already exists: $target" }
}
$script:checks = New-Object 'Collections.Generic.List[string]'
function Assert-Test($Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:checks.Add($Message)
    Write-Output "PASS: $Message"
}
function Invoke-Setup([switch]$Rollback) {
    & $installerPath -Rollback:$Rollback
    if ($LASTEXITCODE -ne 0) { throw "Setup exited with $LASTEXITCODE" }
}
function Wait-Ready([int]$PreviousPid = 0) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $watcherPath = Join-Path $installRoot 'state\watcher.json'
    $archivePath = Join-Path $installRoot 'state\archive-state.json'
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Test-Path $watcherPath) -and (Test-Path $archivePath)) {
            try {
                $watcher = Get-Content $watcherPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $process = Get-Process -Id $watcher.process_id -ErrorAction SilentlyContinue
                $health = Invoke-RestMethod 'http://localhost:57392/health' -TimeoutSec 3
                $expectedIndexes = Join-Path $installRoot '.leann\indexes'
                if ($process -and $watcher.process_id -ne $PreviousPid -and
                    $process.Path -eq $memoryExe -and $health.indexesDir -eq $expectedIndexes -and
                    @($watcher.errors.PSObject.Properties).Count -eq 0) {
                    return $watcher.process_id
                }
            } catch { }
        }
        Start-Sleep -Seconds 3
    }
    if (Test-Path $watcherPath) { Get-Content $watcherPath -Raw -Encoding UTF8 | Write-Output }
    $watcherLog = Join-Path $installRoot 'logs\watcher.log'
    if (Test-Path $watcherLog) { Get-Content $watcherLog -Tail 30 | Write-Output }
    throw 'The scheduled watcher did not build and serve its first index before the timeout.'
}
function Assert-Search {
    $responseText = & $memoryExe search 'Which release colour did the installer test choose?' --top-k 8 --json --include-current
    if ($LASTEXITCODE -ne 0) { throw 'Semantic search failed' }
    $response = $responseText | ConvertFrom-Json
    Assert-Test (@($response.results | Where-Object session_id -eq $fixtureId).Count -gt 0) 'semantic search finds the seeded conversation'
    $conversation = & $memoryExe show $fixtureId --around 'cobalt' --chars 600
    if ($LASTEXITCODE -ne 0) { throw 'Transcript retrieval failed' }
    Assert-Test (($conversation -join [Environment]::NewLine).Contains('cobalt orchard')) 'show recovers the original answer'
}
function Assert-Instructions {
    foreach ($target in $targets) {
        $text = Get-Content $target -Raw -Encoding UTF8
        Assert-Test ([regex]::Matches($text, '<!-- leann-session-memory:start -->').Count -eq 1) "one memory section in $(Split-Path $target -Leaf)"
    }
}
$fixtureId = [guid]::NewGuid().ToString()
$fixtureFolder = Join-Path $profileRoot ".copilot\session-state\$fixtureId"
$originalNote = "Keep my caf$([char]0x00E9) instructions." + [Environment]::NewLine
$postInstallNote = "Added later: $([char]0x03A9)." + [Environment]::NewLine
$startedAt = [DateTime]::UtcNow
$completed = $false
try {
    New-Item -ItemType Directory -Force -Path (Split-Path $targets[0]), $fixtureFolder | Out-Null
    [IO.File]::WriteAllText($targets[0], $originalNote, (New-Object Text.UTF8Encoding $true))
    $events = @(
        @{ type = 'session.start'; timestamp = '2026-10-27T00:00:00Z'; data = @{ sessionId = $fixtureId; producer = 'github-copilot-cli' } },
        @{ type = 'user.message'; id = 'u1'; timestamp = '2026-10-27T00:00:01Z'; data = @{ messageId = 'u1'; content = 'Which release colour did the installer test choose?' } },
        @{ type = 'assistant.message'; id = 'a1'; timestamp = '2026-10-27T00:00:02Z'; data = @{ messageId = 'a1'; phase = 'final'; content = 'The installer test chose cobalt orchard as its release colour.' } }
    )
    $eventLines = @($events | ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress })
    [IO.File]::WriteAllLines((Join-Path $fixtureFolder 'events.jsonl'), $eventLines, (New-Object Text.UTF8Encoding $false))
    Assert-Test (-not (Test-Path (Join-Path $profileRoot '.leann\models\facebook-contriever\model.onnx'))) 'model is absent before first installation'
    Invoke-Setup
    Assert-Test (Test-Path $installState) 'first installation writes committed state'
    Assert-Test (Test-Path (Join-Path $profileRoot '.leann\models\facebook-contriever\model.onnx')) 'first installation downloads the model'
    Assert-Test ($null -ne (Get-ScheduledTask -TaskName $taskName)) 'first installation registers the task'
    Assert-Instructions
    $firstPid = Wait-Ready
    Assert-Test ($firstPid -gt 0) 'the scheduled task starts a healthy watcher and builds the first index'
    Assert-Search
    & $memoryExe self-test
    Assert-Test ($LASTEXITCODE -eq 0) 'installed executable passes self-test'
    $firstState = Get-Content $installState -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Test ($null -eq $firstState.recovery) 'successful installation clears pending recovery'
    $backupPath = $firstState.instructionFiles[0].backup
    $backupBytes = [IO.File]::ReadAllBytes($backupPath)
    Assert-Test ([Convert]::ToBase64String($backupBytes) -eq [Convert]::ToBase64String([Text.Encoding]::UTF8.GetPreamble() + [Text.Encoding]::UTF8.GetBytes($originalNote))) 'initial instruction backup preserves the original bytes'
    Invoke-Setup
    Assert-Instructions
    $secondState = Get-Content $installState -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Test ($secondState.instructionFiles[0].backup -eq $backupPath) 'reinstall preserves the original backup'
    Assert-Test ($null -eq $secondState.recovery) 'reinstall commits without pending recovery'
    $secondPid = Wait-Ready -PreviousPid $firstPid
    Assert-Test ($secondPid -gt 0) 'reinstall restarts a healthy watcher'
    Assert-Search
    [IO.File]::AppendAllText($targets[0], $postInstallNote, (New-Object Text.UTF8Encoding $false))
    $preserved = @{}
    foreach ($folder in @((Join-Path $installRoot 'corpus'), (Join-Path $installRoot '.leann\indexes'))) {
        $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
        Assert-Test ($files.Count -gt 0) "populated data folder: $(Split-Path $folder -Leaf)"
        foreach ($file in $files) { $preserved[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    Invoke-Setup -Rollback
    Assert-Test (-not (Test-Path $installState)) 'rollback removes committed integration state'
    Assert-Test ($null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) 'rollback unregisters the task'
    Assert-Test ($null -eq (Get-Process -Id $secondPid -ErrorAction SilentlyContinue)) 'rollback stops the watcher'
    $remaining = Get-Content $targets[0] -Raw -Encoding UTF8
    Assert-Test ($remaining.Contains($originalNote.Trim()) -and $remaining.Contains($postInstallNote.Trim())) 'rollback preserves Unicode instructions and later edits'
    Assert-Test (-not $remaining.Contains('<!-- leann-session-memory:start -->')) 'rollback removes the memory section'
    Assert-Test (-not (Test-Path $targets[1]) -and -not (Test-Path $targets[2])) 'rollback removes instruction files created only for memory'
    foreach ($path in $preserved.Keys) {
        Assert-Test ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $preserved[$path]) "rollback preserves $(Split-Path $path -Leaf)"
    }
    Assert-Search
    Invoke-Setup -Rollback
    Assert-Test (-not (Test-Path $installState)) 'a repeated rollback succeeds'
    $completed = $true
}
finally {
    $report = [pscustomobject]@{
        passed = $completed
        startedAt = $startedAt.ToString('o')
        finishedAt = [DateTime]::UtcNow.ToString('o')
        windows = [Environment]::OSVersion.VersionString
        checks = @($script:checks.ToArray())
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $env:RUNNER_TEMP 'session-memory-install-report.json') -Encoding UTF8
    if (Test-Path $installState) {
        try { Invoke-Setup -Rollback } catch { Write-Warning "Test cleanup failed: $_" }
    }
}
