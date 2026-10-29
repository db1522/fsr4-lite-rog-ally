<#
.SYNOPSIS
    Installs or updates the FSR 4.1 Lite Game Bar widget from this folder.

.DESCRIPTION
    Trusts the ROG Ally Life signing certificate (one administrator prompt, first time only),
    installs any missing runtime packages, then installs or updates the widget.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# During an update from the widget (Update.cmd sets RAL_UPDATE), explain what's happening with Windows notifications
# under the widget's own name, and reopen Game Bar when the install is done. Manual installs skip all of this.
$appName = 'FSR 4.1 Lite'
function Get-NewVersion($msixPath) {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($msixPath)
        try {
            $entry = $zip.GetEntry('AppxManifest.xml')
            $reader = New-Object IO.StreamReader($entry.Open())
            [xml]$manifest = $reader.ReadToEnd()
            $reader.Close()
            $v = [version]$manifest.Package.Identity.Version
            if ($v.Revision -eq 0) { return "$($v.Major).$($v.Minor).$($v.Build)" } else { return "$v" }
        } finally { $zip.Dispose() }
    } catch { return '' }
}
function Show-UpdateToast([string]$title, [string]$text) {
    if (-not $env:RAL_UPDATE) { return }
    try {
        $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $pkg = Get-AppxPackage -Name 'ROGAllyLife.FSR4Lite' -ErrorAction SilentlyContinue | Select-Object -First 1
        $aumid = if ($pkg) { "$($pkg.PackageFamilyName)!App" } else { '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe' }
        $esc = { param($t) [Security.SecurityElement]::Escape($t) }
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml("<toast launch=`"ms-gamebar:`" activationType=`"protocol`"><visual><binding template=`"ToastGeneric`"><text>$(& $esc $title)</text><text>$(& $esc $text)</text></binding></visual></toast>")
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($aumid).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
    } catch { }
}

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
    $newVersion = Get-NewVersion $msix.FullName
    Show-UpdateToast "Updating $appName$(if ($newVersion) { " to $newVersion" })" 'Game Bar closes for a moment while it installs. Open Game Bar again when it''s done.'
    Start-Sleep -Milliseconds 800
    # Close Game Bar first, then the widget and its helper. Game Bar hosts the widget: closing the widget while Game Bar
    # is open makes Game Bar show "Something went wrong" and keep hold of the widget, which stalls the install until
    # Game Bar is restarted. With Game Bar closed first nothing holds the package, nothing reports a crash, and Windows
    # has no running widget to relaunch as a full-screen app afterwards. Game Bar comes back fresh, with the new
    # version and its icon, on the next press of the Armoury Crate button or Win + G.
    Get-Process -Name 'GameBar', 'GameBarFTServer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'RalFsr.Widget', 'RalFsr.Host' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Write-Host '  Installed' -ForegroundColor Green

    # If Game Bar was opened during the install, close it again so it picks up the new version and icon.
    $gameBar = Get-Process -Name 'GameBar', 'GameBarFTServer' -ErrorAction SilentlyContinue
    if ($gameBar) { $gameBar | Stop-Process -Force -ErrorAction SilentlyContinue }
    Write-Host '  Open Game Bar again to use the updated widget'
    if ($env:RAL_UPDATE) {
        Show-UpdateToast "$appName updated$(if ($newVersion) { " to $newVersion" })" 'Open Game Bar again to use the new version.'
        Start-Sleep -Seconds 2
        try { Start-Process 'ms-gamebar:' } catch { }
    }

    Write-Host "`nOpen Game Bar (the Armoury Crate button on ROG handhelds, Win + G on others), open the Widgets menu and pick FSR 4.1 Lite." -ForegroundColor Green
    Write-Host 'Pin it if you want it to stay over your games.'
    Finish 0
} catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Show-UpdateToast "$appName didn't update" 'Open Game Bar again and press Try again in the widget, or run Install.cmd from the release zip.'
    if ($env:RAL_UPDATE) { try { Start-Process 'ms-gamebar:' } catch { } }
    Finish 1
}
