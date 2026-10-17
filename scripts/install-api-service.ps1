# Installs (or removes) MarocLIS.Api as a Windows service.
#
# The API is the server side of remote biological validation: the biologist's phone reaches it
# through Tailscale (`tailscale serve --bg 5080`), never directly. It listens on 127.0.0.1:5080 and
# speaks plain HTTP on the loopback; Tailscale adds HTTPS and the identity of the device.
#
# Run from an elevated Windows PowerShell on the server, in the folder where the API was published
# (the one holding MarocLIS.Api.exe and appsettings.json), or pass -InstallDirectory:
#
#   .\install-api-service.ps1                         # create and start the service
#   .\install-api-service.ps1 -Account 'LAB\svc-maroclis' -Password (Read-Host -AsSecureString)
#   .\install-api-service.ps1 -Uninstall              # stop and remove it
#
# Without -Account the service runs as LocalSystem (NT AUTHORITY\SYSTEM): with Integrated Security
# in the connection string, SQL Server must then know the machine account (DOMAIN\SERVER$) or, for a
# local SQL Server, NT AUTHORITY\SYSTEM. A dedicated account is preferable: it holds only the
# rights the database needs.
#
# What the script does is no more than `sc.exe create` with the right options; the administration
# manual gives that command for laboratories that prefer to type it.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI.

[CmdletBinding()]
param(
    [string] $InstallDirectory = '.',
    [string] $ServiceName = 'MarocLIS.Api',
    [string] $Account,
    [securestring] $Password,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($Uninstall) {
    if (-not $existing) { Write-Host "Service $ServiceName is not installed."; return }
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    Write-Host "Service $ServiceName removed. The published files and logs were left in place."
    return
}

if ($existing) {
    throw "Service $ServiceName already exists. To update the API: stop the service, copy the new files over, start it. To reinstall: run with -Uninstall first."
}

$directory = (Resolve-Path $InstallDirectory).Path
$exe = Join-Path $directory 'MarocLIS.Api.exe'
if (-not (Test-Path $exe)) { throw "MarocLIS.Api.exe not found in $directory. Publish the API there first." }
if (-not (Test-Path (Join-Path $directory 'appsettings.json'))) { throw "appsettings.json not found in $directory." }

# The ASP.NET Core 10 runtime is required unless the API was published self-contained.
$selfContained = Test-Path (Join-Path $directory 'hostfxr.dll')
if (-not $selfContained) {
    $runtimes = & dotnet --list-runtimes 2>$null
    if (-not ($runtimes -match '^Microsoft\.AspNetCore\.App 10\.')) {
        throw 'The ASP.NET Core Runtime 10 is not installed (dotnet --list-runtimes). Install the "ASP.NET Core Runtime 10" (Hosting Bundle or x64 installer) from https://dotnet.microsoft.com/download/dotnet/10.0.'
    }
}

$parameters = @{
    Name           = $ServiceName
    BinaryPathName = "`"$exe`""
    DisplayName    = 'MarocLIS - validation a distance (API)'
    Description    = 'MarocLIS remote biological validation API. Listens on 127.0.0.1:5080; published to phones by tailscale serve.'
    StartupType    = 'Automatic'
}
if ($Account) {
    if (-not $Password) { throw 'A -Password is required with -Account (use Read-Host -AsSecureString).' }

    # '.\svc-maroclis' is how the service manager writes a local account, but Windows cannot resolve
    # '.' when it grants the folder rights below: the computer's name stands in for it.
    if ($Account.StartsWith('.\')) { $Account = "$env:COMPUTERNAME\" + $Account.Substring(2) }

    # Checked before anything is created, so a mistyped or missing account leaves no half-installed service.
    try {
        (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier]) | Out-Null
    }
    catch {
        throw "Windows account $Account not found. Create it first (New-LocalUser), or check the domain\name spelling."
    }

    $parameters.Credential = New-Object System.Management.Automation.PSCredential($Account, $Password)
}

New-Service @parameters | Out-Null

# Delayed start: SQL Server and Tailscale are up first after a reboot.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
# Restart after a crash: 1 minute, then 1 minute, then 5 minutes; counter reset after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# The service writes its logs under logs\ in its own folder.
$logs = Join-Path $directory 'logs'
if (-not (Test-Path $logs)) { New-Item -ItemType Directory -Path $logs | Out-Null }
if ($Account) {
    $acl = Get-Acl $logs
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($Account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -Path $logs -AclObject $acl
}

Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

try {
    $response = Invoke-WebRequest -Uri 'http://127.0.0.1:5080/' -UseBasicParsing -TimeoutSec 10
    Write-Host "Service $ServiceName started; http://127.0.0.1:5080/ answers $($response.StatusCode)."
}
catch {
    Write-Warning "Service $ServiceName is installed but http://127.0.0.1:5080/ does not answer yet. See $logs and the Windows event log."
}

Write-Host ''
Write-Host 'Next: publish it to the phones through Tailscale (MagicDNS and HTTPS certificates enabled):'
Write-Host '  tailscale serve --bg 5080'
Write-Host 'Never use tailscale funnel: it would put the API on the Internet.'
