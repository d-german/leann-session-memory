param([string]$Installer = (Join-Path $PSScriptRoot '..\Install-SessionMemory.ps1'))
$ErrorActionPreference = 'Stop'
$script:sandbox = Join-Path ([IO.Path]::GetTempPath()) ('session-memory-test-' + [guid]::NewGuid().ToString('N'))
$script:localData = Join-Path $script:sandbox 'local'
$script:testHome = Join-Path $script:sandbox 'profile'
$script:task = $null
$script:failure = ''
$script:failureHit = $false
$script:checks = 0
$script:installerText = [IO.File]::ReadAllText((Resolve-Path $Installer))
$modelCommand = "& (Join-Path `$LeannDir 'leann-dotnet.exe') --setup --model facebook/contriever"
if (-not $script:installerText.Contains($modelCommand)) { throw 'Installer model command changed' }
$script:installerText = $script:installerText.Replace($modelCommand, "throw 'Model download is forbidden in tests'")
$allowed = @('ConvertFrom-Json','ConvertTo-Json','Copy-Item','Disable-ScheduledTask','dotnet','Enable-ScheduledTask','Export-ScheduledTask','ForEach-Object','Get-CimInstance','Get-Content','Get-Date','Get-ScheduledTask','Invoke-RestMethod','Join-Path','New-Item','New-ScheduledTaskAction','New-ScheduledTaskSettingsSet','New-ScheduledTaskTrigger','New-TimeSpan','Out-Null','Register-ScheduledTask','Remove-Item','Select-String','Set-Content','Split-Path','Start-ScheduledTask','Stop-Owned','Stop-Process','Stop-ScheduledTask','Stop-Watcher','Test-Path','Unregister-ScheduledTask','Wait-Process','Where-Object','Write-InstallState','Write-Output')
$allowed += @('Assert-BinaryBackup', 'Remove-BinaryBackup', 'Remove-InstallDirectory', 'Restore-Install')
$parsed = [Management.Automation.Language.Parser]::ParseInput($script:installerText, [ref]$null, [ref]$null)
foreach ($command in $parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true)) {
    $name = $command.GetCommandName()
    if (-not $name -or $name -notin $allowed) { throw "Unapproved installer command: $($command.Extent.Text)" }
}
foreach ($token in @('$HOME', '$env:LOCALAPPDATA', '$PSScriptRoot', '$PSCommandPath')) {
    if (-not $script:installerText.Contains($token)) { throw "Installer path token missing: $token" }
}
$script:installerText = $script:installerText.Replace('$HOME', '$script:testHome').Replace('$env:LOCALAPPDATA', '$script:localData').Replace('$PSScriptRoot', '$script:sandbox').Replace('$PSCommandPath', '$script:fixtureInstaller')
$script:installerText = $script:installerText -replace '(?m)^\s*exit 0\s*$', '    return'
$script:runInstaller = [scriptblock]::Create($script:installerText)
function Assert($Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Fail-Once([string]$Point) {
    if ($script:failure -eq $Point -and -not $script:failureHit) {
        $script:failureHit = $true
        throw "Injected failure: $Point"
    }
}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--list-sdks') { '10.0.100 [test]' }
    if ($args[0] -eq 'publish') {
        $path = Join-Path $script:localData 'LeannSessionMemory\bin\SessionMemory.exe'
        [IO.File]::WriteAllText($path, "published-$script:buildNumber")
        Fail-Once 'publish'
    }
    if ($args[0] -eq 'tool') {
        $dir = Join-Path $script:localData 'LeannSessionMemory\leann'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        [IO.File]::WriteAllText((Join-Path $dir 'leann-dotnet.exe'), "tool-$script:buildNumber")
        Fail-Once 'tool'
    }
}
function Get-CimInstance { @() }
function Invoke-RestMethod { throw 'Network access is forbidden' }
function Invoke-WebRequest { throw 'Network access is forbidden' }
function Stop-Process { throw 'Real process control is forbidden' }
function Wait-Process { throw 'Real process control is forbidden' }
function Get-ScheduledTask { $script:task }
function Export-ScheduledTask { '<Task>previous</Task>' }
function Disable-ScheduledTask {
    if ($script:task) { $script:task.State = 'Disabled' }
    Fail-Once 'stop'
}
function Enable-ScheduledTask { if ($script:task) { $script:task.State = 'Ready' } }
function Stop-ScheduledTask { }
function Unregister-ScheduledTask {
    if ($script:cleanupFail) { $script:cleanupFail = $false; throw 'Injected cleanup failure' }
    $script:task = $null
}
function New-ScheduledTaskAction { 'action' }
function New-ScheduledTaskTrigger { 'trigger' }
function New-ScheduledTaskSettingsSet { 'settings' }
function Register-ScheduledTask {
    param($TaskName, $Action, $Trigger, $Settings, $Xml, [switch]$Force)
    if ($Xml -and $script:cleanupFail) { $script:cleanupFail = $false; throw 'Injected cleanup failure' }
    $script:task = [pscustomobject]@{ State = 'Ready'; Xml = $Xml }
    Fail-Once 'register'
    $script:task
}
function Start-ScheduledTask {
    Fail-Once 'start'
    if ($script:task) { $script:task.State = 'Running' }
}
function Set-Content {
    [CmdletBinding()]
    param(
        [Parameter(Position=0)][string]$Path,
        [Parameter(ValueFromPipeline=$true)][AllowNull()]$Value,
        [string]$Encoding,
        [switch]$NoNewline
    )
    process {
        if (-not [IO.Path]::GetFullPath($Path).StartsWith($script:sandbox + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Write outside sandbox: $Path"
        }
        if ($Path -like '*install.json*') {
            $script:stateWrites++
            if ($script:stateWrites -eq 1) { Fail-Once 'state' }
            if ($script:stateWrites -eq 2) { Fail-Once 'final-state' }
        }
        if ($Path -like '*CLAUDE.md') { Fail-Once 'second-instruction' }
        if ($Path -like '*AGENTS.md') { Fail-Once 'rollback-instruction' }
        Microsoft.PowerShell.Management\Set-Content -Path $Path -Value $Value -Encoding $Encoding -NoNewline:$NoNewline
    }
}
function Invoke-Install([switch]$Rollback) { & $script:runInstaller -Rollback:$Rollback | Out-Null }
function Expect-Failure([string]$Point, [switch]$Rollback) {
    $script:failure = $Point
    $script:failureHit = $false
    $failed = $false
    try { Invoke-Install -Rollback:$Rollback } catch { $failed = $true }
    Assert $failed "$Point should fail"
    Assert $script:failureHit "$Point injection was reached"
    $script:failure = ''
}
function Remove-TestSandbox {
    $fullPath = [IO.Path]::GetFullPath($script:sandbox).TrimEnd('\')
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($fullPath) -notmatch '^session-memory-test-[0-9a-f]{32}$') {
        throw "Unsafe test cleanup path: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
}
function Reset-Fixture {
    Remove-TestSandbox
    New-Item -ItemType Directory -Force -Path $script:testHome, $script:localData, (Join-Path $script:testHome '.codex'), (Join-Path $script:testHome '.leann\models\facebook-contriever') | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:testHome '.leann\models\facebook-contriever\model.onnx'), 'fake')
    [IO.File]::WriteAllText((Join-Path $script:sandbox 'Search-SessionMemory.ps1'), 'fake')
    $script:fixtureInstaller = Join-Path $script:sandbox 'installer.ps1'
    [IO.File]::WriteAllText($script:fixtureInstaller, 'fake')
    $script:task = $null
    $script:failure = ''
    $script:failureHit = $false
    $script:stateWrites = 0
    $script:cleanupFail = $false
    $script:buildNumber = 1
}
$agents = Join-Path $script:testHome '.codex\AGENTS.md'
$claude = Join-Path $script:testHome '.claude\CLAUDE.md'
$state = Join-Path $script:localData 'LeannSessionMemory\state\install.json'
$original = "Custom $([char]0x03A9) instructions" + [Environment]::NewLine
try {
    foreach ($point in @('second-instruction', 'register', 'start', 'state', 'final-state')) {
        Reset-Fixture
        [IO.File]::WriteAllText($agents, $original, (New-Object Text.UTF8Encoding $true))
        $bytes = [IO.File]::ReadAllBytes($agents)
        Expect-Failure $point
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($agents)) -eq [Convert]::ToBase64String($bytes)) "$point restores original bytes"
        Assert (-not (Test-Path $claude)) "$point removes newly created instruction"
        Assert (-not (Test-Path $state)) "$point leaves no successful install state"
        Assert ($null -eq $script:task) "$point removes new task"
    }
    Reset-Fixture
    [IO.File]::WriteAllText($agents, $original, (New-Object Text.UTF8Encoding $true))
    Invoke-Install
    $initialState = Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json
    Invoke-Install
    $newState = Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert (@([regex]::Matches((Get-Content $agents -Raw -Encoding UTF8), '<!-- leann-session-memory:start -->')).Count -eq 1) 'reinstall has one instruction block'
    Assert ($newState.instructionFiles[0].backup -eq $initialState.instructionFiles[0].backup) 'reinstall retains initial backup metadata'
    $beforeFailedReinstall = [IO.File]::ReadAllBytes($agents)
    $beforeFailedState = [IO.File]::ReadAllBytes($state)
    Expect-Failure 'start'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($agents)) -eq [Convert]::ToBase64String($beforeFailedReinstall)) 'failed reinstall restores instructions'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($state)) -eq [Convert]::ToBase64String($beforeFailedState)) 'failed reinstall restores state'
    Assert ($script:task.State -eq 'Running') 'failed reinstall restores running task'
    $corpus = Join-Path $script:localData 'LeannSessionMemory\corpus.txt'
    [IO.File]::WriteAllText($corpus, 'keep')
    [IO.File]::AppendAllText($agents, ("Post-install edit $([char]0x03A9)" + [Environment]::NewLine))
    Invoke-Install -Rollback
    $remaining = Get-Content $agents -Raw -Encoding UTF8
    Assert ($remaining.Contains("Custom $([char]0x03A9)") -and $remaining.Contains("Post-install edit $([char]0x03A9)")) 'rollback preserves custom Unicode and later edits'
    Assert (-not $remaining.Contains('<!-- leann-session-memory:start -->')) 'rollback removes integration block'
    Assert (-not (Test-Path $claude)) 'rollback removes newly created empty instruction file'
    Assert (Test-Path $corpus) 'rollback preserves corpus'
    Invoke-Install -Rollback
    Assert (Test-Path $corpus) 'repeated rollback preserves corpus'
    Reset-Fixture
    [IO.File]::WriteAllText($agents, $original)
    Invoke-Install
    Expect-Failure 'rollback-instruction' -Rollback
    Assert (Test-Path $state) 'failed rollback keeps recovery metadata'
    Invoke-Install -Rollback
    Assert (-not (Test-Path $state)) 'rollback can be retried'
    Reset-Fixture
    [IO.File]::WriteAllText($agents, $original)
    $script:cleanupFail = $true
    Expect-Failure 'register'
    Assert (Test-Path $state) 'failed install cleanup keeps recovery metadata'
    $recovery = Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($null -ne $recovery.recovery) 'recovery metadata contains snapshots'

    foreach ($point in @('stop', 'publish', 'tool')) {
        Reset-Fixture
        [IO.File]::WriteAllText($agents, $original)
        Invoke-Install
        $binFile = Join-Path $script:localData 'LeannSessionMemory\bin\SessionMemory.exe'
        $toolFile = Join-Path $script:localData 'LeannSessionMemory\leann\leann-dotnet.exe'
        $oldBin = [IO.File]::ReadAllBytes($binFile)
        $oldTool = [IO.File]::ReadAllBytes($toolFile)
        $oldState = [IO.File]::ReadAllBytes($state)
        $oldInstructions = [IO.File]::ReadAllBytes($agents)
        $script:buildNumber = 2
        Expect-Failure $point
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($binFile)) -eq [Convert]::ToBase64String($oldBin)) "$point restores binary"
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($toolFile)) -eq [Convert]::ToBase64String($oldTool)) "$point restores tool"
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($state)) -eq [Convert]::ToBase64String($oldState)) "$point restores previous metadata"
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($agents)) -eq [Convert]::ToBase64String($oldInstructions)) "$point restores instructions"
        Assert ($script:task.State -eq 'Running') "$point restores running task"
    }

    Reset-Fixture
    [IO.File]::WriteAllText($agents, $original)
    Invoke-Install
    $binFile = Join-Path $script:localData 'LeannSessionMemory\bin\SessionMemory.exe'
    $toolFile = Join-Path $script:localData 'LeannSessionMemory\leann\leann-dotnet.exe'
    $oldBin = [IO.File]::ReadAllBytes($binFile)
    $oldTool = [IO.File]::ReadAllBytes($toolFile)
    $oldState = [IO.File]::ReadAllBytes($state)
    $oldInstructions = [IO.File]::ReadAllBytes($agents)
    $script:buildNumber = 2
    $script:cleanupFail = $true
    Expect-Failure 'register'
    Assert (Test-Path $state) 'interrupted recovery retains journal'
    $script:cleanupFail = $true
    $retryFailed = $false
    try { Invoke-Install -Rollback } catch { $retryFailed = $true }
    Assert $retryFailed 'interrupted recovery can fail again'
    Assert (Test-Path $state) 'repeated recovery failure retains journal'
    Invoke-Install -Rollback
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($binFile)) -eq [Convert]::ToBase64String($oldBin)) 'pending rollback restores previous binary'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($toolFile)) -eq [Convert]::ToBase64String($oldTool)) 'pending rollback restores previous tool'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($state)) -eq [Convert]::ToBase64String($oldState)) 'pending rollback restores previous state'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($agents)) -eq [Convert]::ToBase64String($oldInstructions)) 'pending rollback restores previous instructions'
    Assert ($script:task.State -eq 'Running') 'pending rollback restores previous task'
    Reset-Fixture
    [IO.File]::WriteAllText($agents, $original)
    Invoke-Install
    $previous = Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:buildNumber = 2
    $script:cleanupFail = $true
    Expect-Failure 'register'
    Invoke-Install
    $afterRecovery = Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($afterRecovery.instructionFiles[0].backup -eq $previous.instructionFiles[0].backup) 'normal install recovers previous backup metadata first'
    Assert (@([regex]::Matches((Get-Content $agents -Raw -Encoding UTF8), '<!-- leann-session-memory:start -->')).Count -eq 1) 'normal install after recovery keeps one block'
    Assert ($script:task.State -eq 'Running') 'normal install after recovery starts task'
    Write-Output "PASS: $script:checks assertions"
}
finally {
    Remove-TestSandbox
}
