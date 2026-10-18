# Install-time helper of the MarocLIS setup program (installer/MarocLIS.iss).
#
# Setup.exe extracts this script to its temporary folder and runs it, elevated, once per step:
#
#   -Action SetConnectionString  writes Database:ConnectionString into a freshly installed
#                                appsettings.json (never called on an existing one)
#   -Action ConfigureServer      server type: SQL Browser, Windows firewall for the LAN, the
#                                MarocLIS database and the logins the workstations and the API
#                                service sign in with
#   -Action InstallService       server type, first install: scripts/install-api-service.ps1
#   -Action InstallBackupTask    server type: the backup folder, its rights for SQL Server, and the
#                                scheduled task "MarocLIS - Sauvegarde" (scripts/maroclis-backup.ps1)
#   -Action StopService          upgrade: stop MarocLIS.Api before its files are replaced
#   -Action StartService         upgrade: start it again afterwards
#
# Every run appends to -LogFile. Exit codes: 0 done, 1 failed, 3 done with a warning that the
# setup program shows on its last page.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('SetConnectionString', 'ConfigureServer', 'InstallService', 'StopService', 'StartService',
                 'InstallBackupTask')]
    [string] $Action,
    [string] $File,
    [string] $Server,
    [string] $Instance = 'SQLEXPRESS',
    [string] $Database = 'MarocLIS',
    [string] $UsersGroup = 'MarocLIS-Utilisateurs',
    [string] $ApiDirectory,
    [string] $ServiceName = 'MarocLIS.Api',
    [string] $BackupScript,
    [string] $BackupFolder = 'C:\MarocLIS\Sauvegardes',
    [string] $BackupTimes = '22:00',
    [int] $RetentionDays = 14,
    [ValidateSet('true', 'false')]
    [string] $BackupEnabled = 'true',
    [string] $LogFile
)

$ErrorActionPreference = 'Stop'
$script:warnings = 0

function Write-Log([string] $message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $Action, $message
    Write-Host $line
    if ($LogFile) {
        $folder = Split-Path -Parent $LogFile
        if ($folder -and -not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
        Add-Content -Path $LogFile -Value $line -Encoding UTF8
    }
}

function Write-Warn([string] $message) {
    $script:warnings++
    Write-Log "WARNING: $message"
}

# --- appsettings.json -------------------------------------------------------------------------

# Rewrites the value of the first "ConnectionString" key (the one under "Database") as text, so
# the comments ("//" keys), the order and the layout of the file stay exactly as shipped.
function Set-ConnectionString {
    if (-not $File -or -not (Test-Path $File)) { throw "appsettings.json not found: $File" }
    if (-not $Server) { throw '-Server is required.' }

    $value = "Server=$Server;Database=$Database;Integrated Security=true"
    $json = $value.Replace('\', '\\').Replace('"', '\"')

    $bytes = [IO.File]::ReadAllBytes($File)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)

    $pattern = [regex] '("ConnectionString"\s*:\s*)"(?:[^"\\]|\\.)*"'
    if (-not $pattern.IsMatch($text)) { throw "No ConnectionString key in $File." }
    $text = $pattern.Replace($text, { param($m) $m.Groups[1].Value + '"' + $json + '"' }, 1)

    [IO.File]::WriteAllText($File, $text, (New-Object Text.UTF8Encoding($hasBom)))
    Write-Log "$File -> $value"
}

# --- SQL Server -------------------------------------------------------------------------------

function Get-InstanceId([string] $name) {
    $key = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
    $names = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
    if (-not $names) { return $null }
    return $names.$name
}

function Get-DataSource {
    if ($Instance -eq 'MSSQLSERVER') { return 'localhost' }
    return "localhost\$Instance"
}

function Invoke-Sql([System.Data.SqlClient.SqlConnection] $connection, [string] $sql) {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = 120
    [void] $command.ExecuteNonQuery()
}

function Open-Sql {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = Get-DataSource
    $builder['Initial Catalog'] = 'master'
    $builder['Integrated Security'] = $true
    $builder['Connect Timeout'] = 15

    # The service may still be starting right after SQL Server setup.
    $deadline = (Get-Date).AddMinutes(2)
    while ($true) {
        try {
            $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
            $connection.Open()
            return $connection
        }
        catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 5
        }
    }
}

function Get-QuotedName([string] $name) { return '[' + $name.Replace(']', ']]') + ']' }
function Get-Literal([string] $text) { return "N'" + $text.Replace("'", "''") + "'" }

# The local group whose members may use MarocLIS. Workstation users authenticate to SQL Server
# with their Windows account (Integrated Security): domain users or groups are added to it, and
# in a workgroup the server needs a local account of the same name and password for each user.
function Initialize-UsersGroup {
    $exists = $false
    try { $exists = [bool] ([ADSI] "WinNT://$env:COMPUTERNAME/$UsersGroup,group").Name } catch { $exists = $false }
    if (-not $exists) {
        $computer = [ADSI] "WinNT://$env:COMPUTERNAME,computer"
        $created = $computer.Create('group', $UsersGroup)
        $created.SetInfo()
        $created.Put('Description', 'Comptes autorises a utiliser MarocLIS (connexion Windows a SQL Server).')
        $created.SetInfo()
        Write-Log "Local group $UsersGroup created."
    }
    else {
        Write-Log "Local group $UsersGroup already exists."
    }

    return "$env:COMPUTERNAME\$UsersGroup"
}

function Grant-DatabaseAccess([System.Data.SqlClient.SqlConnection] $connection, [string] $login) {
    $q = Get-QuotedName $login
    $l = Get-Literal $login
    Invoke-Sql $connection "IF SUSER_ID($l) IS NULL CREATE LOGIN $q FROM WINDOWS;"
    # The workstations apply the migrations when they start, so their role is db_owner; the API
    # service gets the same role as the workstations (administration manual, remote validation).
    # Matched on the SID, not the name: the account that created the database is already in it,
    # as dbo, and dbo can be neither created again nor added to a role.
    Invoke-Sql $connection ("USE " + (Get-QuotedName $Database) + "; " +
        "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE sid = SUSER_SID($l)) CREATE USER $q FOR LOGIN $q; " +
        "DECLARE @user sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID($l)); " +
        "IF @user <> N'dbo' AND IS_ROLEMEMBER(N'db_owner', @user) = 0 EXEC (N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@user) + N';');")
    Write-Log "$login may sign in to SQL Server and is db_owner of $Database."
}

function Set-FirewallRule([string] $name, [hashtable] $rule) {
    Remove-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    try {
        New-NetFirewallRule -DisplayName $name -Group 'MarocLIS' -Direction Inbound -Action Allow `
            -Profile Any -RemoteAddress LocalSubnet @rule | Out-Null
        Write-Log "Firewall rule '$name' created (inbound, local subnet)."
    }
    catch {
        Write-Warn "Firewall rule '$name' could not be created: $($_.Exception.Message)"
    }
}

function Set-ServerConfiguration {
    $instanceId = Get-InstanceId $Instance
    if (-not $instanceId) { throw "SQL Server instance $Instance not found in the registry." }
    $setupKey = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId"
    Write-Log "Instance $Instance (id $instanceId)."

    # The workstations reach the server's SQL Server over the LAN.
    $tcp = Get-ItemProperty -Path "$setupKey\MSSQLServer\SuperSocketNetLib\Tcp" -ErrorAction SilentlyContinue
    if ($tcp -and $tcp.Enabled -ne 1) {
        Write-Warn "TCP/IP is disabled on ${Instance}: workstations cannot connect until it is enabled (SQL Server Configuration Manager) and the SQL Server service restarted."
    }

    # A named instance (SERVER\SQLEXPRESS) is found by the workstations through SQL Server Browser.
    if ($Instance -ne 'MSSQLSERVER') {
        $browser = Get-Service -Name 'SQLBrowser' -ErrorAction SilentlyContinue
        if ($browser) {
            Set-Service -Name 'SQLBrowser' -StartupType Automatic
            if ($browser.Status -ne 'Running') { Start-Service -Name 'SQLBrowser' }
            Write-Log 'SQL Server Browser: automatic, running.'
        }
        else {
            Write-Warn 'SQL Server Browser service not found: workstations will need the instance port in the server name.'
        }
    }

    # Firewall: the database engine (whatever port it listens on) and the Browser, open to the
    # local subnet only. Any profile, because a small laboratory's network is often classed Public.
    $binRoot = (Get-ItemProperty -Path "$setupKey\Setup" -ErrorAction SilentlyContinue).SQLBinRoot
    $engine = if ($binRoot) { Join-Path $binRoot 'sqlservr.exe' } else { $null }
    if ($engine -and (Test-Path $engine)) {
        Set-FirewallRule "MarocLIS - SQL Server ($Instance)" @{ Program = $engine }
    }
    else {
        Write-Warn "sqlservr.exe of $Instance not found; firewall rule for the database engine not created."
    }
    if ($Instance -ne 'MSSQLSERVER') {
        Set-FirewallRule 'MarocLIS - SQL Server Browser' @{ Protocol = 'UDP'; LocalPort = 1434 }
    }

    # Database and logins. The database is created empty: the first MarocLIS start applies the
    # schema (DatabaseInitializer). It has to exist now so the logins can be given a user in it.
    $group = Initialize-UsersGroup
    $connection = Open-Sql
    try {
        $db = Get-Literal $Database
        Invoke-Sql $connection "IF DB_ID($db) IS NULL CREATE DATABASE $(Get-QuotedName $Database);"
        Write-Log "Database $Database present."
        Grant-DatabaseAccess $connection $group
        # The administrator installing the server is its first user, and is given a login of
        # their own: a group added now only reaches their token at their next sign-in, and
        # BUILTIN\Administrators only counts in an elevated process.
        Grant-DatabaseAccess $connection ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
        # MarocLIS.Api runs as LocalSystem (install-api-service.ps1 without -Account).
        Grant-DatabaseAccess $connection 'NT AUTHORITY\SYSTEM'
        # The scheduled backup runs as SYSTEM too. db_owner may BACKUP, but RESTORE VERIFYONLY, which
        # checks every backup it writes and those taken from a workstation, needs CREATE DATABASE.
        # SYSTEM can already stop SQL Server and start it in single-user mode, so this gives it
        # nothing it could not take.
        Invoke-Sql $connection 'GRANT CREATE ANY DATABASE TO [NT AUTHORITY\SYSTEM];'
        Write-Log 'NT AUTHORITY\SYSTEM may verify backups (CREATE ANY DATABASE).'
    }
    finally {
        $connection.Dispose()
    }
}

# --- Scheduled backup ------------------------------------------------------------------------

$BackupTaskName = 'MarocLIS - Sauvegarde'

# The account SQL Server runs as: it, not this script nor the task, writes the backup files.
function Get-SqlServiceAccount {
    $name = if ($Instance -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { 'MSSQL$' + $Instance }
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name = '" + $name + "'") -ErrorAction SilentlyContinue
    if ($service -and $service.StartName) { return $service.StartName }
    return "NT Service\$name"
}

# The folder, SQL Server's right to write in it, and the task. The task reads its schedule from the
# database (Parametres > Sauvegardes in MarocLIS); the values given here are written there by the
# task the first time it finds none, so an upgrade keeps what the laboratory chose since.
function Install-BackupTask {
    if (-not $BackupScript -or -not (Test-Path $BackupScript)) { throw "Backup script not found: $BackupScript" }
    if ($BackupFolder -match '["<>|?*]') { throw "Invalid backup folder: $BackupFolder" }
    # "E:" from the setup program is the root of E: (a trailing "\" would escape its closing quote).
    if ($BackupFolder -match '^[A-Za-z]:$') { $BackupFolder += '\' }

    if ($BackupFolder -match '^[A-Za-z]:\\') {
        if (-not (Test-Path -LiteralPath $BackupFolder)) {
            try {
                New-Item -ItemType Directory -Path $BackupFolder -Force | Out-Null
                Write-Log "Backup folder $BackupFolder created."
            }
            catch { Write-Warn "Backup folder $BackupFolder could not be created: $($_.Exception.Message)" }
        }
        if (Test-Path -LiteralPath $BackupFolder) {
            $account = Get-SqlServiceAccount
            # LocalSystem is SYSTEM, which every folder of a local disk already admits.
            if ($account -eq 'LocalSystem') { $account = 'NT AUTHORITY\SYSTEM' }
            & icacls.exe $BackupFolder /grant "${account}:(OI)(CI)M" | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Log "$account (SQL Server service) may write into $BackupFolder." }
            else { Write-Warn "icacls could not give $account the right to write into $BackupFolder (exit code $LASTEXITCODE)." }
        }
    }
    elseif ($BackupFolder.StartsWith('\\')) {
        # A share is reached by SQL Server's service as the server's computer account.
        Write-Warn "Backup folder $BackupFolder is a network share: its share and NTFS permissions must let the computer account $env:USERDOMAIN\$env:COMPUTERNAME`$ write, or every backup will fail (MarocLIS shows it in Parametres > Sauvegardes)."
    }
    else {
        Write-Warn "Backup folder $BackupFolder is neither a local path nor a share; the backups will fail until it is corrected in MarocLIS (Parametres > Sauvegardes)."
    }

    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Server "{1}" -Database "{2}" ' +
                  '-DefaultFolder "{3}" -DefaultTimes "{4}" -DefaultRetentionDays {5} -DefaultEnabled {6}') -f
                 $BackupScript, (Get-DataSource), $Database, $BackupFolder.TrimEnd('\'), $BackupTimes, $RetentionDays, $BackupEnabled

    $action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory (Split-Path -Parent $BackupScript)
    # Every 15 minutes, from midnight today, without end; and at startup, so a server switched off
    # at the backup time catches up as soon as it is on again.
    $repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 15)
    $startup = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 4)

    Register-ScheduledTask -TaskName $BackupTaskName -Action $action -Trigger @($repeat, $startup) -Principal $principal `
        -Settings $settings -Force `
        -Description 'Sauvegarde de la base MarocLIS selon les reglages de MarocLIS (Parametres > Sauvegardes).' | Out-Null
    Write-Log "Scheduled task '$BackupTaskName' registered (every 15 minutes, as SYSTEM): $powershell $arguments"
}

# --- MarocLIS.Api service ---------------------------------------------------------------------

function Install-ApiService {
    $script = Join-Path $ApiDirectory 'install-api-service.ps1'
    if (-not (Test-Path $script)) { throw "$script not found." }
    & $script -InstallDirectory $ApiDirectory -ServiceName $ServiceName *>&1 | ForEach-Object { Write-Log "$_" }
}

function Stop-ApiService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { Write-Log "Service $ServiceName not installed."; return }
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    $service.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))
    Write-Log "Service $ServiceName stopped."
}

function Start-ApiService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { Write-Warn "Service $ServiceName not installed."; return }
    if ($service.Status -ne 'Running') { Start-Service -Name $ServiceName }
    Write-Log "Service $ServiceName started."
}

try {
    switch ($Action) {
        'SetConnectionString' { Set-ConnectionString }
        'ConfigureServer'     { Set-ServerConfiguration }
        'InstallService'      { Install-ApiService }
        'StopService'         { Stop-ApiService }
        'StartService'        { Start-ApiService }
        'InstallBackupTask'   { Install-BackupTask }
    }
}
catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    exit 1
}

if ($script:warnings -gt 0) { exit 3 }
exit 0
