param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Query,
    [ValidateRange(1, 50)]
    [int]$TopK = 8,
    [switch]$Current,
    [switch]$IncludeCurrent,
    [switch]$Json
)

$Options = @('--top-k', $TopK)
if ($Current) { $Options += '--current' }
if ($IncludeCurrent) { $Options += '--include-current' }
if ($Json) { $Options += '--json' }
& (Join-Path $PSScriptRoot 'SessionMemory.exe') search $Query @Options
exit $LASTEXITCODE
