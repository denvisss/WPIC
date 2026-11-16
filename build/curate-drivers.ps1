param(
    [Parameter(Mandatory = $true)][ValidateSet('index', 'extract', 'finalize')][string]$Mode,
    [string]$Pack = '',
    [string]$PackRoot = (Join-Path $PSScriptRoot '..\Drivers_26.8_LAN'),
    [string]$Work = (Join-Path $PSScriptRoot 'curated')
)
$ErrorActionPreference = 'Stop'
$seven = Join-Path $PackRoot 'Tools\7za64.exe'
$stage = Join-Path $Work 'staging'
$out = Join-Path $Work 'payload\DriverPack'
New-Item -ItemType Directory -Force -Path $stage, $out | Out-Null

function Open-Index([string]$name) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $seven
    $psi.Arguments = "e -so `"$PackRoot\Index\DIA\$name.txt`""
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($psi)
}

function Test-NicRow([string[]]$f) {
    return $f.Count -ge 7 -and $f[0] -match '^(PCI\\|USB\\)' -and $f[1] -match '\\(10x64|11x64)\\'
}

function Get-DateKey([string]$s) {
    $d = $s.Split(',')[0].Split('/')
    if ($d.Count -ne 3) { throw "Invalid driver date: $s" }
    return [int]$d[2] * 10000 + [int]$d[0] * 100 + [int]$d[1]
}

if ($Mode -eq 'index') {
    $byDevice = @{}
    $p = Open-Index $Pack
    $rows = 0
    while ($null -ne ($line = $p.StandardOutput.ReadLine())) {
        $f = $line.Split("`t")
        if (-not (Test-NicRow $f)) { continue }
        $rows++
        $folder = $f[1].Substring(0, $f[1].LastIndexOf('\'))
        $os = if ($f[1] -match '\\11x64\\') { '11x64' } else { '10x64' }
        $key = $f[0] + '|' + $os
        $entry = @{ d = (Get-DateKey $f[3]); v = [version]$f[3].Split(',')[1]; f = $folder }
        if (-not $byDevice.ContainsKey($key)) { $byDevice[$key] = @{} }
        $old = $byDevice[$key][$folder]
        if (-not $old -or $entry.d -gt $old.d -or ($entry.d -eq $old.d -and $entry.v -gt $old.v)) {
            $byDevice[$key][$folder] = $entry
        }
    }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "7za index pass1 exit $($p.ExitCode)" }
    $p.Dispose()

    $best = @{}
    $kept = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $byDevice.Keys) {
        $versions = [System.Collections.Generic.HashSet[string]]::new()
        $selected = @($byDevice[$key].Values | Sort-Object @{Expression={$_.d}; Descending=$true}, @{Expression={$_.v}; Descending=$true}, @{Expression={$_.f}; Descending=$false} |
            Where-Object { $versions.Add("$($_.d)|$($_.v)") } | Select-Object -First 2)
        $best[$key] = $selected
        foreach ($entry in $selected) { [void]$kept.Add($entry.f) }
    }

    $sw = [System.IO.StreamWriter]::new((Join-Path $Work "index-$Pack.tsv"), $false, [System.Text.UTF8Encoding]::new($false))
    $p = Open-Index $Pack
    $emitted = 0
    try {
        while ($null -ne ($line = $p.StandardOutput.ReadLine())) {
            $f = $line.Split("`t")
            if (-not (Test-NicRow $f)) { continue }
            $folder = $f[1].Substring(0, $f[1].LastIndexOf('\'))
            $os = if ($f[1] -match '\\11x64\\') { '11x64' } else { '10x64' }
            $selected = $best[($f[0] + '|' + $os)]
            if (-not ($selected | Where-Object { $_.f -eq $folder -and $_.d -eq (Get-DateKey $f[3]) -and $_.v -eq [version]$f[3].Split(',')[1] })) { continue }
            $f[1] = "$Pack\$($f[1])"
            $sw.WriteLine(($f -join "`t"))
            $emitted++
        }
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) { throw "7za index pass2 exit $($p.ExitCode)" }
    } finally { $sw.Dispose(); $p.Dispose() }
    $kept | Sort-Object | Set-Content (Join-Path $Work "kept-$Pack.txt") -Encoding UTF8
    Write-Output "INDEX $Pack rows=$rows keptFolders=$($kept.Count) emitted=$emitted"
}

if ($Mode -eq 'extract') {
    $listFile = Join-Path $Work "list-$Pack.txt"
    Get-Content (Join-Path $Work "kept-$Pack.txt") | ForEach-Object { "$_\*" } | Set-Content $listFile -Encoding UTF8
    $dest = Join-Path $stage $Pack
    if (Test-Path $dest) { throw "Use a fresh work directory: $dest already exists" }
    New-Item -ItemType Directory -Path $dest | Out-Null
    & $seven x -y "-o$dest" "-ir@$listFile" "$PackRoot\Drivers\$Pack.7z" | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "7za extract exit $LASTEXITCODE" }
    $size = (Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Output ("EXTRACT {0} staged={1:N0} MB" -f $Pack, $size)
}

if ($Mode -eq 'finalize') {
    if (Test-Path "$out\WPIC-Drivers.7z") { throw 'Archive already exists; use a fresh work directory' }
    $parts = @(Get-ChildItem $Work -Filter 'index-*.tsv' | Sort-Object Name)
    if ($parts.Count -ne 4) { throw 'Expected four curated indexes (Intel, Others, Realtek, WLAN)' }
    $sw = [System.IO.StreamWriter]::new((Join-Path $out 'index.tsv'), $false, [System.Text.UTF8Encoding]::new($false))
    $netInf = @{}
    $folders = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($part in $parts) {
            foreach ($line in [System.IO.File]::ReadLines($part.FullName)) {
                $inf = $line.Split("`t")[1]
                if (-not $netInf.ContainsKey($inf)) {
                    $path = Join-Path $stage $inf
                    if (-not (Test-Path -LiteralPath $path)) { throw "Missing INF: $inf" }
                    $netInf[$inf] = [IO.File]::ReadAllText($path) -match '(?im)^\s*Class\s*=\s*Net\s*(;[^\r\n]*)?$'
                }
                if (-not $netInf[$inf]) { continue }
                [void]$folders.Add($inf.Substring(0, $inf.LastIndexOf('\')))
                $sw.WriteLine($line)
            }
        }
    } finally { $sw.Dispose() }
    if ($folders.Count -eq 0) { throw 'No network driver folders selected' }
    $archiveList = Join-Path $Work 'archive-files.txt'
    $folders | Sort-Object | Set-Content $archiveList -Encoding UTF8
    Push-Location $stage
    try {
        & $seven a -t7z -m0=LZMA2 -mx=7 -ms=on -mmt=on "$out\WPIC-Drivers.7z" "@$archiveList" | Select-Object -Last 3
        if ($LASTEXITCODE -ne 0) { throw "7za repack exit $LASTEXITCODE" }
    } finally { Pop-Location }
    Write-Output "FILTER networkInf=$(@($netInf.Values | Where-Object { $_ }).Count) excludedInf=$(@($netInf.Values | Where-Object { -not $_ }).Count) folders=$($folders.Count)"
    & $seven t "$out\WPIC-Drivers.7z" | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw 'Archive integrity check failed' }
    Copy-Item $seven (Join-Path $out '7za64.exe')
    Set-Content (Join-Path $out 'pack.version') '26.8.2' -Encoding ASCII
    Get-ChildItem $out | ForEach-Object { Write-Output ("OUT {0} {1:N1} MiB" -f $_.Name, ($_.Length / 1MB)) }
}
