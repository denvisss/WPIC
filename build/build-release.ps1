param([string]$Iscc = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pack = Join-Path $PSScriptRoot 'payload\DriverPack'
foreach ($name in @('WPIC-Drivers.7z', 'index.tsv', 'pack.version', '7za64.exe')) {
    if (-not (Test-Path (Join-Path $pack $name))) { throw "Missing driver payload: $name" }
}
$winget = Join-Path $PSScriptRoot 'payload\winget'
foreach ($name in @('AppInstaller_x64.msix', 'Microsoft.UI.Xaml.2.8.x64.appx', 'Microsoft.VCLibs.x64.14.00.Desktop.appx')) {
    $sig = Get-AuthenticodeSignature (Join-Path $winget $name)
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
        throw "Invalid Microsoft signature: $name"
    }
}
if (-not $Iscc) {
    $Iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 compiler not found; supply -Iscc' }

$tests = Join-Path $root 'tests\WPIC.Tests\WPIC.Tests.csproj'
$app = Join-Path $root 'src\WPIC\WPIC.csproj'
$csproj = [xml](Get-Content $app -Raw)
$version = $csproj.Project.PropertyGroup.Version
# версия установщика всегда следует за csproj
$iss = Join-Path $PSScriptRoot 'installer.iss'
$issText = [regex]::Replace([IO.File]::ReadAllText($iss), '#define AppVersion "[^"]*"', "#define AppVersion `"$version`"")
[IO.File]::WriteAllText($iss, $issText, [System.Text.UTF8Encoding]::new($true))
& dotnet test $tests -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

& dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $PSScriptRoot 'publish') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

& $Iscc (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }

& dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $PSScriptRoot 'publish-portable') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed' }

$release = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
Move-Item (Join-Path $PSScriptRoot "installer\WPIC_$version-full.exe") $release -Force
Copy-Item (Join-Path $PSScriptRoot 'publish-portable\WindowsPostInstallClient.exe') (Join-Path $release "WPIC_$version-portable.exe") -Force
Remove-Item (Join-Path $PSScriptRoot 'publish-portable') -Recurse -Force

# zip-варианты релиза: тот же exe, упакованный без сжатия (exe уже сжат)
$sevenZip = Join-Path $pack '7za64.exe'
foreach ($variant in @('full', 'portable')) {
    $exe = Join-Path $release "WPIC_$version-$variant.exe"
    $zip = Join-Path $release "WPIC_$version-$variant.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    & $sevenZip a -tzip -mx=1 -bd $zip $exe | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Zip failed: $variant" }
}

Get-ChildItem $release -File | Where-Object { $_.Name -like 'WPIC_*.exe' -or $_.Name -like 'WPIC_*.zip' } | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($_.FullName + '.sha256', "$hash  $($_.Name)`r`n")
    Write-Output ("ARTIFACT {0} {1:N1} MiB SHA256={2}" -f $_.Name, ($_.Length / 1MB), $hash)
}
Write-Output ("VERSION " + $version)
