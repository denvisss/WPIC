param([string]$Tag = 'v1.1.0', [string]$Title = 'WPIC 1.1.0', [string]$AssetFilter = 'WPIC_1.1.0-*')
# Creates the GitHub release and uploads all artifacts from build\release with their .sha256 sidecars.
# The token is read from the git credential store and never printed.
$ErrorActionPreference = 'Stop'
$releaseDir = Join-Path $PSScriptRoot 'release'
$notesPath = Join-Path $releaseDir 'notes.md'
if (-not (Test-Path $notesPath)) { throw "Missing release notes: $notesPath" }

$fill = 'protocol=https' + [Environment]::NewLine + 'host=github.com' + [Environment]::NewLine
$token = $null
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'git'
$psi.Arguments = 'credential fill'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($psi)
$process.StandardInput.Write($fill)
$process.StandardInput.Close()
while ($null -ne ($line = $process.StandardOutput.ReadLine())) {
    if ($line.StartsWith('password=')) { $token = $line.Substring(9) }
}
$process.WaitForExit()
if (-not $token) { throw 'No GitHub credential found' }

$headers = @{
    'Authorization' = 'Bearer ' + $token
    'Accept' = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$bodyObject = @{
    tag_name = $Tag
    target_commitish = 'main'
    name = $Title
    body = [System.IO.File]::ReadAllText($notesPath)
    draft = $false
    prerelease = $false
}
$body = $bodyObject | ConvertTo-Json

$existing = $null
try {
    $existing = Invoke-RestMethod -Method Get -Headers $headers -TimeoutSec 60 -Uri "https://api.github.com/repos/denvisss/WPIC/releases/tags/$Tag"
} catch { }
if ($existing -and $existing.id) {
    $releaseId = $existing.id
    Write-Output ("RELEASE existing id=" + $releaseId)
    $patch = @{ name = $Title; body = $bodyObject.body } | ConvertTo-Json
    Invoke-RestMethod -Method Patch -Headers $headers -TimeoutSec 60 -ContentType 'application/json' -Body ([System.Text.Encoding]::UTF8.GetBytes($patch)) -Uri ("https://api.github.com/repos/denvisss/WPIC/releases/" + $releaseId) | Out-Null
    Write-Output 'RELEASE-NOTES synced'
} else {
    $release = Invoke-RestMethod -Method Post -Headers $headers -TimeoutSec 60 -ContentType 'application/json' -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -Uri 'https://api.github.com/repos/denvisss/WPIC/releases'
    $releaseId = $release.id
    Write-Output ("RELEASE created id=" + $releaseId + " tag=" + $release.tag_name)
}

$uploadBase = 'https://uploads.github.com/repos/denvisss/WPIC/releases/' + $releaseId + '/assets?name='
$releaseNow = Invoke-RestMethod -Method Get -Headers $headers -TimeoutSec 60 -Uri "https://api.github.com/repos/denvisss/WPIC/releases/$releaseId"
foreach ($asset in $releaseNow.assets) {
    if ($asset.name -notlike $AssetFilter) {
        Invoke-RestMethod -Method Delete -Headers $headers -TimeoutSec 60 -Uri "https://api.github.com/repos/denvisss/WPIC/releases/assets/$($asset.id)" | Out-Null
        Write-Output ("REMOVED-ASSET " + $asset.name)
    }
}
$files = Get-ChildItem $releaseDir -File -Filter $AssetFilter | Sort-Object Name
$present = @{}
$releaseNow = Invoke-RestMethod -Method Get -Headers $headers -TimeoutSec 60 -Uri "https://api.github.com/repos/denvisss/WPIC/releases/$releaseId"
foreach ($asset in $releaseNow.assets) { $present[$asset.name] = $asset.size }

$headerFile = Join-Path $env:TEMP ('wpic-upload-' + [Guid]::NewGuid().ToString('N') + '.hdr')
Set-Content -Path $headerFile -Value ("Authorization: Bearer " + $token) -Encoding ASCII
try {
    foreach ($file in $files) {
        if ($present.ContainsKey($file.Name) -and $present[$file.Name] -eq $file.Length) {
            Write-Output ("ASSET-OK " + $file.Name)
            continue
        }
        $uploaded = $false
        for ($attempt = 1; $attempt -le 5 -and -not $uploaded; $attempt++) {
            Write-Output ("UPLOAD-START " + $file.Name + " attempt " + $attempt)
            $code = & curl.exe -sS -o $env:TEMP\wpic-upload-resp.json -w "%{http_code}" `
                -H "@$headerFile" -H "Content-Type: application/octet-stream" `
                --max-time 3600 --speed-time 60 --speed-limit 10240 `
                --data-binary "@$($file.FullName)" `
                ($uploadBase + [Uri]::EscapeDataString($file.Name))
            $exit = $LASTEXITCODE
            if ($exit -eq 0 -and $code -eq 201) {
                Write-Output ("ASSET " + $file.Name + " " + [Math]::Round($file.Length / 1MB, 1) + " MiB")
                $uploaded = $true
            } else {
                Write-Output ("RETRY " + $file.Name + " attempt " + $attempt + " http=" + $code + " curl_exit=" + $exit)
                Start-Sleep -Seconds 10
            }
        }
        if (-not $uploaded) { throw "Upload failed after retries: " + $file.Name }
    }
} finally { Remove-Item $headerFile -Force -ErrorAction SilentlyContinue }
Write-Output 'PUBLISH-DONE'
