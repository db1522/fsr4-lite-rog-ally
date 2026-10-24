<#
.SYNOPSIS
    Builds the FSR 4 Lite Game Bar widget into a signed, installable release zip.

    You normally don't run this yourself: GitHub runs it when you push a version tag
    (see .github/workflows/release.yml and the README). Run it locally only if you have
    Visual Studio 2022 with the UWP workload and want to build on your own PC.

.DESCRIPTION
    1. Publishes the full-trust helper (src\RalFsr.Host) into src\RalFsr.Widget\Host.
    2. Finds or creates the "CN=ROG Ally Life" code-signing certificate in your user store.
    3. Builds and signs the widget package with MSBuild (Visual Studio 2022, UWP workload).
    4. Puts the .msix, its runtime dependencies, the public .cer and the installer into dist\FSR4Lite_x64.zip.

    The certificate is self-signed and can be thrown away. Updates install over older versions as long as the
    publisher name (CN=ROG Ally Life) stays the same; users just approve the certificate again in Install.cmd.
#>
param(
    [string]$Configuration = 'Release',

    # Release version, e.g. 0.2.0 (GitHub passes the tag). Written into the package manifest.
    [string]$Version,

    # Fail instead of creating a certificate (optional, for reusing one certificate across releases).
    [switch]$RequireExistingCertificate
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$widgetDir = Join-Path $root 'src\RalFsr.Widget'
$hostProject = Join-Path $root 'src\RalFsr.Host\RalFsr.Host.csproj'
$dist = Join-Path $root 'dist'
$subject = 'CN=ROG Ally Life'

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

if ($Version) {
    Step "Setting version $Version"
    $parts = $Version.Split('.')
    while ($parts.Count -lt 4) { $parts += '0' }
    $full = ($parts[0..3] -join '.')
    $manifestPath = Join-Path $widgetDir 'Package.appxmanifest'
    $manifest = Get-Content $manifestPath -Raw
    $manifest = $manifest -replace '(<Identity [^>]*Version=")[^"]+(")', "`${1}$full`${2}"
    Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
}

Step 'Publishing the helper'
$hostOut = Join-Path $widgetDir 'Host'
if (Test-Path $hostOut) { Remove-Item $hostOut -Recurse -Force }
dotnet publish $hostProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $hostOut
if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed.' }
Get-ChildItem $hostOut -Filter *.pdb | Remove-Item -Force

Step 'Finding the signing certificate'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert -and $RequireExistingCertificate) {
    throw "No '$subject' certificate with a private key was found."
}
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'ROG Ally Life package signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "  Created certificate $($cert.Thumbprint)"
} else {
    Write-Host "  Using $($cert.Thumbprint)"
}

Step 'Building the widget package'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio 2022 not found. Install it with the "Universal Windows Platform development" workload.' }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found in the Visual Studio install.' }

$packageDir = Join-Path $dist 'packages\'
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
& $msbuild (Join-Path $widgetDir 'RalFsr.Widget.csproj') /restore /m /v:minimal `
    /p:Configuration=$Configuration /p:Platform=x64 `
    /p:AppxPackageDir=$packageDir /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxBundle=Never `
    /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=true `
    /p:PackageCertificateThumbprint=$($cert.Thumbprint)
if ($LASTEXITCODE -ne 0) { throw 'Widget build failed.' }

Step 'Assembling the release'
$msix = Get-ChildItem $packageDir -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw 'No .msix was produced.' }

$release = Join-Path $dist 'FSR4Lite_x64'
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $release 'Dependencies') -Force | Out-Null

Copy-Item $msix.FullName (Join-Path $release 'FSR4Lite_x64.msix')
$deps = Join-Path $msix.DirectoryName 'Dependencies\x64'
if (Test-Path $deps) { Copy-Item (Join-Path $deps '*.appx') (Join-Path $release 'Dependencies') }
Export-Certificate -Cert $cert -FilePath (Join-Path $release 'ROGAllyLife.cer') | Out-Null
$installer = Join-Path $root 'src\Installer'
Copy-Item (Join-Path $installer 'install.ps1') $release
Copy-Item (Join-Path $installer 'Install.cmd.template') (Join-Path $release 'Install.cmd')

$zip = Join-Path $dist 'FSR4Lite_x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $release '*') -DestinationPath $zip
Write-Host "`nDone: $zip" -ForegroundColor Green
