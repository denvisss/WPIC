param(
    [int]$KeepVersions = 1,
    [string]$PackDir = (Join-Path $PSScriptRoot 'payload\DriverPack'),
    [string]$Work = (Join-Path $PSScriptRoot 'prune')
)
$ErrorActionPreference = 'Stop'
$seven = Join-Path $PackDir '7za64.exe'
$stage = Join-Path $Work 'stage'
$out = Join-Path $Work 'payload\DriverPack'
if (Test-Path $Work) { throw "Use a fresh work directory: $Work exists" }
New-Item -ItemType Directory -Force -Path $stage, $out | Out-Null

# 1) extract the current pack
& $seven x -y "-o$stage" (Join-Path $PackDir 'WPIC-Drivers.7z') | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "extract exit $LASTEXITCODE" }

# 2) per device+OS keep only the newest driver versions
function Get-DateKey([string]$s) {
    $d = $s.Split(',')[0].Split('/')
    if ($d.Count -ne 3) { throw "Invalid driver date: $s" }
    return [int]$d[2] * 10000 + [int]$d[0] * 100 + [int]$d[1]
}

$rows = [System.Collections.Generic.List[object[]]]::new()
$byKey = @{}
foreach ($line in [IO.File]::ReadLines((Join-Path $PackDir 'index.tsv'))) {
    $f = $line.Split("`t")
    if ($f.Count -lt 4) { throw "Unexpected index row: $line" }
    $os = if ($f[1] -match '\\11x64\\') { '11x64' } else { '10x64' }
    $key = $f[0] + '|' + $os
    $entry = [object[]]@($f, $key, (Get-DateKey $f[3]), [version]($f[3].Split(',')[1]))
    $rows.Add($entry)
    if (-not $byKey.ContainsKey($key)) { $byKey[$key] = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase) }
    [void]$byKey[$key].Add($f[3])
}

$selected = @{}
foreach ($key in $byKey.Keys) {
    $best = @($byKey[$key] | ForEach-Object { $p = $_.Split(','); [pscustomobject]@{ dv = $_; d = (Get-DateKey $_); v = [version]$p[1] } } |
        Sort-Object @{Expression = 'd'; Descending = $true }, @{Expression = 'v'; Descending = $true } |
        Select-Object -First $KeepVersions -ExpandProperty dv)
    $selected[$key] = $best
}

$keptRows = [System.Collections.Generic.List[string[]]]::new()
foreach ($r in $rows) {
    if ($selected[$r[1]] -contains $r[0][3]) { $keptRows.Add($r[0]) }
}
if ($keptRows.Count -eq 0) { throw 'Pruning removed every index row' }
Write-Output ("ROWS total={0} kept={1} keepVersions={2}" -f $rows.Count, $keptRows.Count, $KeepVersions)

# 3) folder-level dedupe: identical INF(name+date,version) sets collapse to the first folder
$sigs = @{}
foreach ($f in $keptRows) {
    $i = $f[1].LastIndexOf('\')
    $folder = $f[1].Substring(0, $i)
    if (-not $sigs.ContainsKey($folder)) { $sigs[$folder] = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase) }
    [void]$sigs[$folder].Add(($f[1].Substring($i + 1) + '|' + $f[3]))
}
$canonical = @{}
foreach ($folder in ($sigs.Keys | Sort-Object)) {
    $sig = ($sigs[$folder] | Sort-Object) -join ';'
    if (-not $canonical.ContainsKey($sig)) { $canonical[$sig] = $folder }
    $canonical[$folder] = $canonical[$sig]
}
$dropped = @($sigs.Keys | Where-Object { $canonical[$_] -ne $_ })
Write-Output ("FOLDERS used={0} deduped={1} dropped={2}" -f $sigs.Count, ($sigs.Count - $dropped.Count), $dropped.Count)
foreach ($d in $dropped) { Write-Output "  DROP $d -> $($canonical[$d])" }

# 4) rewrite index to canonical folders
$sw = [System.IO.StreamWriter]::new((Join-Path $Work 'index.tsv'), $false, [System.Text.UTF8Encoding]::new($false))
try {
    foreach ($f in $keptRows) {
        $i = $f[1].LastIndexOf('\')
        $folder = $f[1].Substring(0, $i)
        $c = $canonical[$folder]
        if ($c -ne $folder) { $f[1] = $c + $f[1].Substring($folder.Length) }
        if (-not (Test-Path -LiteralPath (Join-Path $stage $f[1]))) { throw "Missing INF after rewrite: $($f[1])" }
        $sw.WriteLine(($f -join "`t"))
    }
} finally { $sw.Dispose() }
$keptFolders = @($sigs.Keys | Where-Object { $canonical[$_] -eq $_ } | Sort-Object)

# 5) repack with a large LZMA2 dictionary (unused staging folders are simply not archived)
$list = Join-Path $Work 'archive-files.txt'
$keptFolders | Set-Content $list -Encoding UTF8
Push-Location $stage
try {
    & $seven a -t7z -m0=LZMA2 -mx=7 -md=512m -ms=on -mmt=on (Join-Path $out 'WPIC-Drivers.7z') "@$list" | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "repack exit $LASTEXITCODE" }
} finally { Pop-Location }

# 6) integrity check
& $seven t (Join-Path $out 'WPIC-Drivers.7z')
if ($LASTEXITCODE -ne 0) { throw 'Archive integrity check failed' }

$oldArchive = Get-Item (Join-Path $PackDir 'WPIC-Drivers.7z')
$newArchive = Get-Item (Join-Path $out 'WPIC-Drivers.7z')
$stageSize = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Output ("OLD archive {0:N1} MiB" -f ($oldArchive.Length / 1MB))
Write-Output ("NEW archive {0:N1} MiB (stage {1:N0} MiB, folders {2})" -f ($newArchive.Length / 1MB), ($stageSize / 1MB), $keptFolders.Count)
Write-Output "DONE - copy index.tsv and WPIC-Drivers.7z from the OUT dir to the payload when satisfied"
