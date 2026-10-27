# Installs (or removes) MarocLIS Web as a Windows service, with its own HTTPS certificate.
#
# MarocLIS Web is the laboratory's second interface, served by the server to the browsers of its
# local network on https://<server>:5443 (src/MarocLIS.Web). It is how the workstations still on
# Windows 7, 8 or 8.1, which cannot run the Windows application (.NET 10 needs Windows 10), work:
# the setup program's "Poste Web" type gives them a browser that can (Firefox ESR 115 when they
# have none) and trusts this server's certificate.
#
# Run from an elevated Windows PowerShell on the server, in the folder where the web was published
# (the one holding MarocLIS.Web.exe and appsettings.json), or pass -InstallDirectory:
#
#   .\install-web-service.ps1                     # certificate, firewall, service; started
#   .\install-web-service.ps1 -RenewCertificate   # a new certificate (after a rename or a new address)
#   .\install-web-service.ps1 -Uninstall          # stop and remove the service and its firewall rule
#
# Running it again is harmless: an existing service is kept (and started), an existing certificate
# reused while it has more than 30 days to run.
#
# The certificate. Self-signed, RSA 2048 / SHA-256, valid 10 years, its private key not exportable;
# its names (subjectAltName) are the server's name, its full DNS name, "localhost" and every IPv4
# address of the server, so https://SERVEUR:5443, https://serveur.domaine.local:5443 and
# https://192.168.1.10:5443 all match it. It is put in LocalMachine\My, where Kestrel finds it by its
# subject (appsettings.json, Kestrel:Endpoints:Https:Certificate), and in LocalMachine\Root so the
# server's own browser trusts it. Its public part is exported to certificat-serveur.cer next to the
# web (served at /certificat-serveur.cer to the workstations' setup) and, when the server has a
# share named MarocLIS, into that share; its SHA-256 fingerprint is written to certificat-serveur.txt
# and printed, for whoever installs a workstation to compare.
#
# Uninstalling removes the service and the firewall rule; the certificate stays, so a reinstall
# keeps the workstations that already trust it working.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI.

[CmdletBinding()]
param(
    [string] $InstallDirectory = '.',
    [string] $ServiceName = 'MarocLIS.Web',
    [int] $Port = 5443,
    [switch] $RenewCertificate,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$FirewallRule = "MarocLIS - Web (HTTPS $Port)"
$FriendlyName = 'MarocLIS Web'
$CertificateFile = 'certificat-serveur.cer'
$FingerprintFile = 'certificat-serveur.txt'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($Uninstall) {
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
        Write-Host "Service $ServiceName removed."
    }
    else {
        Write-Host "Service $ServiceName is not installed."
    }
    Remove-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue
    Write-Host "Firewall rule '$FirewallRule' removed. The certificate and the published files were left in place."
    return
}

$directory = (Resolve-Path $InstallDirectory).Path
$exe = Join-Path $directory 'MarocLIS.Web.exe'
$settings = Join-Path $directory 'appsettings.json'
if (-not (Test-Path $exe)) { throw "MarocLIS.Web.exe not found in $directory. Publish the web there first." }
if (-not (Test-Path $settings)) { throw "appsettings.json not found in $directory." }

# --- The certificate ---------------------------------------------------------------------------

$computer = $env:COMPUTERNAME
$subjectName = "MarocLIS Web $computer"

function Get-ServerNames {
    $names = New-Object System.Collections.Generic.List[string]
    $names.Add($computer)
    try {
        $fqdn = [System.Net.Dns]::GetHostEntry($computer).HostName
        if ($fqdn -and $fqdn -ne $computer -and $fqdn.Contains('.')) { $names.Add($fqdn) }
    }
    catch { }
    $names.Add('localhost')
    return $names | Select-Object -Unique
}

function Get-ServerAddresses {
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } |
        ForEach-Object { $_.IPAddress })
    return @($addresses + '127.0.0.1') | Select-Object -Unique
}

function Get-Fingerprint([System.Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = $sha.ComputeHash($certificate.RawData)
    return (($hash | ForEach-Object { $_.ToString('X2') }) -join ':')
}

function Get-ServerCertificate {
    return Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq "CN=$subjectName" -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}

function New-ServerCertificate {
    $names = @(Get-ServerNames)
    $addresses = @(Get-ServerAddresses)
    $san = (@($names | ForEach-Object { "DNS=$_" }) + @($addresses | ForEach-Object { "IPAddress=$_" })) -join '&'
    $certificate = New-SelfSignedCertificate -Subject "CN=$subjectName" -FriendlyName $FriendlyName `
        -Type SSLServerAuthentication -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation 'Cert:\LocalMachine\My' -TextExtension @("2.5.29.17={text}$san")
    Write-Host "Certificate created for $($names -join ', '), $($addresses -join ', ') (valid until $($certificate.NotAfter.ToString('yyyy-MM-dd')))."
    return $certificate
}

$certificate = Get-ServerCertificate
if ($RenewCertificate -or -not $certificate -or $certificate.NotAfter -lt (Get-Date).AddDays(30)) {
    $previous = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=$subjectName" })
    $certificate = New-ServerCertificate
    # The former ones go: Kestrel would take the newest anyway, and the workstations must not keep
    # trusting a certificate this server no longer uses.
    foreach ($old in $previous) {
        if ($old.Thumbprint -eq $certificate.Thumbprint) { continue }
        Remove-Item -Path "Cert:\LocalMachine\My\$($old.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item -Path "Cert:\LocalMachine\Root\$($old.Thumbprint)" -ErrorAction SilentlyContinue
        Write-Host "Former certificate $($old.Thumbprint) removed."
    }
}
else {
    Write-Host "Certificate $($certificate.Thumbprint) kept (valid until $($certificate.NotAfter.ToString('yyyy-MM-dd')))."
    $san = ($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1)
    $sanText = if ($san) { $san.Format($false) } else { '' }
    $missing = @(Get-ServerAddresses | Where-Object { $sanText -notmatch ('(?<![\d.])' + [regex]::Escape($_) + '(?![\d.])') })
    if ($missing.Count -gt 0) {
        Write-Warning "The certificate does not name $($missing -join ', ') (a new address?). Browsers that use it will warn; use the server's name, or run this script with -RenewCertificate and import the new certificate on the workstations."
    }
}

# The server's own browser trusts it.
$root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
$root.Open('ReadWrite')
try {
    if (-not ($root.Certificates | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })) {
        $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(,$certificate.RawData)
        $root.Add($public)
        Write-Host 'Certificate added to the trusted roots of this server.'
    }
}
finally { $root.Close() }

# Its public part, for the workstations.
$fingerprint = Get-Fingerprint $certificate
$export = Join-Path $directory $CertificateFile
[IO.File]::WriteAllBytes($export, $certificate.RawData)
$text = @(
    'MarocLIS Web - certificat du serveur',
    '',
    "Serveur            : $computer",
    "Adresse            : https://${computer}:$Port/",
    "Empreinte SHA-256  : $fingerprint",
    "Valable jusqu'au   : $($certificate.NotAfter.ToString('yyyy-MM-dd'))",
    '',
    "Sur chaque poste, le programme d'installation (type Poste Web) affiche l'empreinte du certificat",
    "qu'il recoit : elle doit etre identique a celle-ci, caractere pour caractere."
)
Set-Content -Path (Join-Path $directory $FingerprintFile) -Value $text -Encoding UTF8
Write-Host "Public certificate exported to $export"

$share = Get-SmbShare -Name 'MarocLIS' -ErrorAction SilentlyContinue
if ($share -and $share.Path -and (Test-Path $share.Path)) {
    Copy-Item -Path $export -Destination (Join-Path $share.Path $CertificateFile) -Force
    Copy-Item -Path (Join-Path $directory $FingerprintFile) -Destination (Join-Path $share.Path $FingerprintFile) -Force
    Write-Host "Also copied to the share \\$computer\MarocLIS."
}

Write-Host "SHA-256 fingerprint: $fingerprint"

# --- appsettings.json: Kestrel uses it ---------------------------------------------------------

# Text, not JSON objects: the comments ("//" keys), the order and the layout of the file stay as
# shipped, as maroclis-setup.ps1 does for the connection string.
$bytes = [IO.File]::ReadAllBytes($settings)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$json = [IO.File]::ReadAllText($settings, [Text.Encoding]::UTF8)
$certificateBlock = '"Certificate": { "Subject": "' + $subjectName + '", "Store": "My", "Location": "LocalMachine", "AllowInvalid": true }'
if ($json -match '"Certificate"\s*:') {
    if ($json -match '"Subject"\s*:\s*"MarocLIS Web [^"]*"') {
        $json = [regex]::Replace($json, '"Subject"\s*:\s*"MarocLIS Web [^"]*"', '"Subject": "' + $subjectName + '"')
        Write-Host "appsettings.json: Kestrel uses the certificate $subjectName."
    }
    else {
        Write-Warning 'appsettings.json already names another certificate for Kestrel: kept as it is.'
    }
}
else {
    $endpoint = [regex] '("Https"\s*:\s*\{)(\s*)'
    if (-not $endpoint.IsMatch($json)) { throw "No Kestrel:Endpoints:Https section in $settings." }
    $json = $endpoint.Replace($json, { param($m) $m.Groups[1].Value + $m.Groups[2].Value + $certificateBlock + ',' + $m.Groups[2].Value }, 1)
    Write-Host "appsettings.json: Kestrel uses the certificate $subjectName."
}
[IO.File]::WriteAllText($settings, $json, (New-Object Text.UTF8Encoding($hasBom)))

# --- Firewall: the local network only ------------------------------------------------------------

Remove-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $FirewallRule -Group 'MarocLIS' -Direction Inbound -Action Allow -Protocol TCP `
    -LocalPort $Port -RemoteAddress LocalSubnet -Profile Any | Out-Null
Write-Host "Firewall rule '$FirewallRule' created (inbound TCP $Port, local subnet)."

# --- The service --------------------------------------------------------------------------------

if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'MarocLIS - version Web' `
        -Description "MarocLIS Web: the laboratory's interface in the browser, https://<server>:$Port for the local network." `
        -StartupType Automatic | Out-Null
    # Delayed start: SQL Server is up first after a reboot.
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    # Restart after a crash: 1 minute, then 1 minute, then 5 minutes; counter reset after a day.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Write-Host "Service $ServiceName created (LocalSystem, delayed automatic start)."
}

$logs = Join-Path $directory 'logs'
if (-not (Test-Path $logs)) { New-Item -ItemType Directory -Path $logs | Out-Null }

# A running service reads its certificate at start: restarted when it was running.
$service = Get-Service -Name $ServiceName
if ($service.Status -eq 'Running') { Restart-Service -Name $ServiceName -Force } else { Start-Service -Name $ServiceName }
Start-Sleep -Seconds 5

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $response = Invoke-WebRequest -Uri "https://localhost:$Port/connexion" -UseBasicParsing -TimeoutSec 20
    Write-Host "Service $ServiceName started; https://localhost:$Port/connexion answers $($response.StatusCode)."
}
catch {
    Write-Warning "Service $ServiceName is installed but https://localhost:$Port/ does not answer yet ($($_.Exception.Message)). See $logs and the Windows event log."
}

Write-Host ''
Write-Host "Workstations: https://${computer}:$Port/ ; install the "Poste Web" type of the setup program on each one."
