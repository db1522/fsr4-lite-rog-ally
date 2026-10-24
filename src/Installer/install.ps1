<#
.SYNOPSIS
    Installs or updates the FSR 4 Lite Game Bar widget from this folder.

.DESCRIPTION
    Trusts the ROG Ally Life signing certificate (one administrator prompt, first time only),
    installs any missing runtime packages, then installs or updates the widget.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Finish($code) {
    if ($env:FSR4LITE_PAUSE) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}

try {
    $msix = Get-ChildItem $here -Filter *.msix | Select-Object -First 1
    $cerPath = Join-Path $here 'ROGAllyLife.cer'
    if (-not $msix) { throw 'The .msix file is missing. Unzip the whole download before running this.' }
    if (-not (Test-Path $cerPath)) { throw 'ROGAllyLife.cer is missing. Unzip the whole download before running this.' }

    Step 'Checking the signing certificate'
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerPath
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
    if ($trusted) {
        Write-Host '  Already trusted'
    } else {
        Write-Host '  Windows will ask for administrator permission once, to trust the ROG Ally Life certificate.'
        $cmd = "Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-Command', $cmd)
        if ($p.ExitCode -ne 0) { throw 'The certificate was not trusted, so Windows will refuse the package.' }
        Write-Host '  Trusted'
    }

    Step 'Installing runtime packages'
    foreach ($dep in Get-ChildItem (Join-Path $here 'Dependencies') -Filter *.appx -ErrorAction SilentlyContinue) {
        try {
            Add-AppxPackage -Path $dep.FullName -ErrorAction Stop
            Write-Host "  $($dep.Name)"
        } catch {
            # A same or newer version is already installed, which is fine.
            Write-Host "  $($dep.Name) (already present)"
        }
    }

    Step 'Installing the widget'
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Write-Host '  Installed' -ForegroundColor Green

    Write-Host "`nOpen Game Bar with Win + G, open the Widgets menu and pick FSR 4 Lite." -ForegroundColor Green
    Write-Host 'Pin it if you want it to stay over your games.'
    Finish 0
} catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Finish 1
}
