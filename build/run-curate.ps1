param([string]$Mode, [string]$Pack)
$log = Join-Path $PSScriptRoot 'run.log'
try {
    & (Join-Path $PSScriptRoot 'curate-drivers.ps1') -Mode $Mode -Pack $Pack *> $log
}
catch {
    ($_.Exception.Message + "`n" + $_.ScriptStackTrace) | Out-File $log -Encoding utf8 -Append
    Get-Content $log -Tail 8
    exit 1
}
Get-Content $log -Tail 8