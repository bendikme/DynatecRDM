<#
.SYNOPSIS
    Trusts DYNATEC Remote Desktop Manager as a publisher of .rdp files, so Remote Desktop stops
    warning before every connection. Run once per machine, as an administrator.

.DESCRIPTION
    Remote Desktop shows "the publisher of this remote connection cannot be identified" for every
    unsigned .rdp file, on every launch. A user cannot accept that permanently: putting a
    certificate in the user's Trusted Publishers and Trusted Root stores, or listing it in
    PublisherBypassList, does not silence it. Windows only honours the machine policy "Specify SHA1
    thumbprints of certificates representing trusted .rdp publishers", which is why this needs
    administrator rights.

    The script creates a code-signing certificate for the current user if there is none, then adds
    its thumbprint to that policy. From then on the application signs the files it generates and
    they open without a prompt, with every setting intact.

    Connections that do not need an .rdp file are already started as "mstsc /v:host" and never
    showed the warning in the first place; this is only needed for connections that use settings
    the command line cannot express, such as a fixed connection quality, an RD Gateway, redirection
    options or a custom authentication level.

.PARAMETER Remove
    Removes this machine's trust for the certificate again.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build\trust-publisher.ps1
#>
[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'

$subject   = 'CN=DYNATEC Remote Desktop Manager'
$policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$valueName = 'TrustedCertThumbprints'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'This must run as an administrator: the trusted-publisher list is a machine policy.' -ForegroundColor Red
    Write-Host 'Right-click PowerShell, choose "Run as administrator", and run it again.' -ForegroundColor Yellow
    exit 1
}

function Get-Thumbprints {
    if (-not (Test-Path $policyKey)) { return @() }
    $raw = (Get-ItemProperty -Path $policyKey -ErrorAction SilentlyContinue).$valueName
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    return @($raw -split '[,; ]' | Where-Object { $_ })
}

function Set-Thumbprints([string[]]$Values) {
    New-Item -Path $policyKey -Force | Out-Null
    if ($Values.Count -eq 0) {
        Remove-ItemProperty -Path $policyKey -Name $valueName -ErrorAction SilentlyContinue
    } else {
        New-ItemProperty -Path $policyKey -Name $valueName `
            -Value ($Values -join ',') -PropertyType String -Force | Out-Null
    }
}

$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if ($Remove) {
    if (-not $cert) { Write-Host 'No signing certificate found; nothing to remove.'; exit 0 }
    $kept = @(Get-Thumbprints | Where-Object { $_ -ne $cert.Thumbprint })
    Set-Thumbprints $kept
    Write-Host "Removed $($cert.Thumbprint) from the trusted .rdp publishers." -ForegroundColor Green
    Write-Host 'Remote Desktop will warn about generated .rdp files again.'
    exit 0
}

if (-not $cert) {
    Write-Host 'Creating a code-signing certificate for this user...'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(10)
    Write-Host "  created $($cert.Thumbprint)"
} else {
    Write-Host "Using the existing certificate $($cert.Thumbprint)"
}

$current = Get-Thumbprints
if ($current -contains $cert.Thumbprint) {
    Write-Host 'This machine already trusts that certificate.' -ForegroundColor Green
} else {
    Set-Thumbprints @($current + $cert.Thumbprint)
    Write-Host "Added $($cert.Thumbprint) to the trusted .rdp publishers." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. Start a connection again - it should open without the security warning.'
Write-Host 'If it still warns, sign out and back in so the policy is re-read.'
Write-Host ''
Write-Host 'To roll this back:  .\build\trust-publisher.ps1 -Remove'
