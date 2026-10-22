param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Query,
    [ValidateRange(1, 50)]
    [int]$TopK = 8
)

& (Join-Path $PSScriptRoot 'SessionMemory.exe') search $Query --top-k $TopK
exit $LASTEXITCODE
