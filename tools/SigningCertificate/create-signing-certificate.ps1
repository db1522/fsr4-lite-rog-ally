<#
.SYNOPSIS
    Creates the permanent "CN=ROG Ally Life" signing certificate for FSR 4.1 Lite, once.

.DESCRIPTION
    Run this once on your own PC. It creates a 10-year code-signing certificate with the same publisher name as the
    widget, then writes three files to a folder on your Desktop:

      ROGAllyLife-signing.pfx        the certificate WITH its private key. Keep it safe (password manager or USB
                                     stick). Never upload or share it: anyone with it can sign packages your
                                     players' devices trust.
      SIGNING_PFX_BASE64.txt         paste into the GitHub secret SIGNING_PFX_BASE64
      SIGNING_PFX_PASSWORD.txt       paste into the GitHub secret SIGNING_PFX_PASSWORD

    After pasting both secrets, delete the two .txt files. Keep the .pfx.
#>
$ErrorActionPreference = 'Stop'

$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ROG Ally Life signing certificate'
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host 'Creating the certificate...' -ForegroundColor Cyan
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=ROG Ally Life' -KeyUsage DigitalSignature `
    -FriendlyName 'ROG Ally Life package signing (permanent)' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears(10) -KeyExportPolicy Exportable `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

# A long random password for the .pfx.
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)
$secure = ConvertTo-SecureString $password -AsPlainText -Force

$pfx = Join-Path $out 'ROGAllyLife-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)) | Set-Content (Join-Path $out 'SIGNING_PFX_BASE64.txt') -NoNewline
$password | Set-Content (Join-Path $out 'SIGNING_PFX_PASSWORD.txt') -NoNewline

# The certificate only needs to live in the .pfx and in GitHub, so take it out of this PC's store again.
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"

Write-Host ''
Write-Host "Done. Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "Files are in: $out"
Write-Host ''
Write-Host 'Next: in your GitHub repo, Settings > Secrets and variables > Actions > New repository secret:'
Write-Host '  SIGNING_PFX_BASE64    = contents of SIGNING_PFX_BASE64.txt'
Write-Host '  SIGNING_PFX_PASSWORD  = contents of SIGNING_PFX_PASSWORD.txt'
Write-Host 'Then delete the two .txt files and keep ROGAllyLife-signing.pfx somewhere safe.'
Start-Process explorer.exe $out
Read-Host "`nPress Enter to close" | Out-Null
