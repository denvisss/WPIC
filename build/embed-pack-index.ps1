param(
    [string]$Payload = (Join-Path $PSScriptRoot 'payload\DriverPack'),
    [string]$Work = (Join-Path $PSScriptRoot 'pruned')
)
# Repacks WPIC-Drivers.7z with index.tsv and pack.version stored INSIDE the archive
# (added first, so 7za e -so reads them without walking the whole solid stream).
# Reuses the existing extraction in $Work\stage when present. ASCII-only, PS 5.1.
$ErrorActionPreference = 'Stop'
$seven = Join-Path $Payload '7za64.exe'
$source = Join-Path $Payload 'WPIC-Drivers.7z'
$stage = Join-Path $Work 'stage'
if (-not (Test-Path $source)) { throw "Missing source archive: $source" }
if (-not (Test-Path (Join-Path $Payload 'index.tsv'))) { throw "Missing external index.tsv next to the archive" }

if (-not (Test-Path $stage)) {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    & $seven x -y "-o$stage" $source | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "extract exit $LASTEXITCODE" }
}

Copy-Item (Join-Path $Payload 'index.tsv') (Join-Path $stage 'index.tsv') -Force
Copy-Item (Join-Path $Payload 'pack.version') (Join-Path $stage 'pack.version') -Force

$folders = @(Get-ChildItem $stage -Directory)
if ($folders.Count -eq 0) { throw 'No extracted folders found' }
$listFile = Join-Path $Work 'embed-files.txt'
$folders | ForEach-Object { $_.Name } | Sort-Object | Set-Content $listFile -Encoding UTF8

$out = Join-Path $Work 'WPIC-Drivers.7z'
if (Test-Path $out) { Remove-Item $out -Force }
Push-Location $stage
try {
    & $seven a -t7z -m0=LZMA2 -mx=7 -md=512m -ms=on -mmt=on $out index.tsv pack.version "@$listFile" | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "repack exit $LASTEXITCODE" }
} finally { Pop-Location }
& $seven t $out | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw 'integrity check failed' }
& $seven l $out index.tsv | Select-Object -Last 4

Copy-Item $source (Join-Path $Work 'WPIC-Drivers-prev.7z') -Force
Copy-Item $out $source
Remove-Item (Join-Path $Payload 'index.tsv') -Force
Remove-Item (Join-Path $Payload 'pack.version') -Force

$oldSize = (Get-Item (Join-Path $Work 'WPIC-Drivers-prev.7z')).Length / 1MB
$newSize = (Get-Item $source).Length / 1MB
Write-Output ("DONE folders={0} size {1:N1} -> {2:N1} MiB" -f $folders.Count, $oldSize, $newSize)
