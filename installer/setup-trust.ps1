<#
.SYNOPSIS
    Sets up machine-wide trust for the .rdp files DYNATEC Remote Desktop Manager signs, so Remote
    Desktop (mstsc and the modern msrdc client) stops warning before every connection.

.DESCRIPTION
    Windows only silences the "publisher cannot be identified" warning for a signed .rdp file when
    the signing certificate's thumbprint is listed in the machine policy
    "Specify SHA1 thumbprints of certificates representing trusted .rdp publishers"
    (HKLM\...\Terminal Services\TrustedCertThumbprints). That policy needs administrator rights,
    which is why the installer runs this once, elevated.

    It creates ONE machine-level code-signing certificate (LocalMachine\My), trusts it (Root +
    TrustedPublisher so the signature validates and names the publisher), grants every account read
    access to its private key so the app can sign as any user, and lists its thumbprint in the
    policy. The application then signs with this certificate and its connections open without a
    prompt for everyone on the machine.

    Idempotent: run it again and it reuses the existing certificate. Nothing here touches user data.

.PARAMETER Remove
    Removes the certificate and the machine trust again.
#>
[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'

$subject   = 'CN=DYNATEC Remote Desktop Manager'
$policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$valueName = 'TrustedCertThumbprints'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Thumbprints {
    if (-not (Test-Path $policyKey)) { return @() }
    $raw = (Get-ItemProperty -Path $policyKey -ErrorAction SilentlyContinue).$valueName
    if ($null -eq $raw) { return @() }
    # A REG_MULTI_SZ reads back as an array; a REG_SZ as one string. Either way, a SHA1 thumbprint
    # is exactly 40 hex characters, so all separators (and any earlier run that concatenated them
    # with none) are handled by stripping non-hex and slicing into 40-character thumbprints.
    $hex = ((@($raw) -join '') -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $out = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i + 40 -le $hex.Length; $i += 40) {
        $t = $hex.Substring($i, 40)
        if (-not $out.Contains($t)) { $out.Add($t) }
    }
    return $out.ToArray()
}

function Set-Thumbprints([string[]]$Values) {
    New-Item -Path $policyKey -Force | Out-Null
    $unique = @($Values | Where-Object { $_ } | Select-Object -Unique)
    if ($unique.Count -eq 0) {
        Remove-ItemProperty -Path $policyKey -Name $valueName -ErrorAction SilentlyContinue
    } else {
        New-ItemProperty -Path $policyKey -Name $valueName -Value ($unique -join ',') -PropertyType String -Force | Out-Null
    }
}

function Find-MachineCert {
    Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}

# No reason to prompt for elevation when the trust is already correct: the certificate exists and
# the policy value is byte-for-byte what this script would write. Any other state - missing cert,
# missing thumbprint, or a value some earlier run left malformed - falls through to the fix below.
# These reads need no administrator rights, so they run before the elevation step.
if (-not $Remove) {
    $have = Find-MachineCert
    if ($have) {
        $desired = ((Get-Thumbprints) + $have.Thumbprint | Select-Object -Unique) -join ','
        $current = if (Test-Path $policyKey) { (Get-ItemProperty $policyKey -EA SilentlyContinue).$valueName } else { $null }
        if ($current -is [string] -and $current -eq $desired) { exit 0 }
    }
}

# The machine policy needs administrator rights. When the installer runs this unelevated it
# re-launches itself through UAC; declining the prompt simply leaves the app signing with a
# per-user certificate, which still connects (with the prompt) until trust is set up later.
if (-not (Test-Admin)) {
    try {
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"")
        if ($Remove) { $argList += '-Remove' }
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Publisher trust was not set up (elevation declined): $($_.Exception.Message)" -ForegroundColor Yellow
    }
    exit 0
}

if ($Remove) {
    $cert = Find-MachineCert
    if ($cert) {
        Set-Thumbprints (Get-Thumbprints | Where-Object { $_ -ne $cert.Thumbprint })
        foreach ($p in 'My', 'Root', 'TrustedPublisher') {
            Get-ChildItem "Cert:\LocalMachine\$p" -ErrorAction SilentlyContinue |
                Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
                ForEach-Object { Remove-Item $_.PSPath -Force -ErrorAction SilentlyContinue }
        }
        Write-Host 'Removed the DYNATEC RDM publisher trust.'
    }
    exit 0
}

# 1) The signing certificate, machine-wide.
$cert = Find-MachineCert
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
        -KeyLength 2048 -NotAfter (Get-Date).AddYears(10)
    Write-Host "Created machine signing certificate $($cert.Thumbprint)."
} else {
    Write-Host "Reusing machine signing certificate $($cert.Thumbprint)."
}

# 2) Trust it: Root so the chain validates, TrustedPublisher so the publisher is named.
$pub = [Convert]::ToBase64String($cert.Export('Cert'))
$tmp = Join-Path $env:TEMP "dynatec-rdm-trust.cer"
[IO.File]::WriteAllBytes($tmp, [Convert]::FromBase64String($pub))
try {
    foreach ($p in 'Root', 'TrustedPublisher') {
        if (-not (Get-ChildItem "Cert:\LocalMachine\$p" -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
            Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\LocalMachine\$p" | Out-Null
        }
    }
} finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

# 3) Let every account use the private key to sign (the app runs as the user, not as admin).
try {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    $keyName = $rsa.Key.UniqueName
    $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyName"
    if (Test-Path $keyPath) {
        $acl = Get-Acl $keyPath
        $sid = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'Read', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -Path $keyPath -AclObject $acl
        Write-Host 'Granted signing access to the private key.'
    }
} catch {
    Write-Host "Could not grant private-key access: $($_.Exception.Message)" -ForegroundColor Yellow
}

# 4) Trust the thumbprint for .rdp publishers, machine-wide.
$thumbs = Get-Thumbprints
if ($thumbs -notcontains $cert.Thumbprint) { $thumbs += $cert.Thumbprint }
Set-Thumbprints $thumbs

Write-Host 'Publisher trust is set up. Signed connections will open without a security prompt.' -ForegroundColor Green
exit 0
