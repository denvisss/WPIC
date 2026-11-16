$ErrorActionPreference = 'Stop'
$dst = Join-Path $PSScriptRoot 'payload\winget'
$tmp = Join-Path $PSScriptRoot ('winget-fetch\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dst, $tmp | Out-Null
$ProgressPreference = 'SilentlyContinue'

# The signed x64 package avoids bundling other architectures and Windows App Runtime.
$bundle = "$tmp\winget17.msixbundle"
Invoke-WebRequest 'https://github.com/microsoft/winget-cli/releases/download/v1.7.10861/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile $bundle
$seven = Join-Path $PSScriptRoot 'payload\DriverPack\7za64.exe'
& $seven e -y "-o$dst" $bundle 'AppInstaller_x64.msix' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7za extract failed: $LASTEXITCODE" }

Invoke-WebRequest 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile "$dst\Microsoft.VCLibs.x64.14.00.Desktop.appx"
Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.UI.Xaml/2.8.6' -OutFile "$tmp\xaml.nupkg.zip"
Expand-Archive "$tmp\xaml.nupkg.zip" -DestinationPath "$tmp\xaml" -Force
$appx = Get-ChildItem "$tmp\xaml" -Recurse -Filter 'Microsoft.UI.Xaml.2.8.appx' | Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
Copy-Item $appx.FullName "$dst\Microsoft.UI.Xaml.2.8.x64.appx" -Force
foreach ($file in Get-ChildItem $dst -File) {
    $signature = Get-AuthenticodeSignature $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
        throw "Invalid Microsoft signature: $($file.Name)"
    }
}
Remove-Item $tmp -Recurse -Force
Get-ChildItem $dst | ForEach-Object { Write-Output ("WINGET {0} {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }