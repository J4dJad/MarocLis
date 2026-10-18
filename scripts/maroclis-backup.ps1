# Scheduled backup of the MarocLIS database.
#
# SQL Server Express has no SQL Agent: the setup program (server type) registers this script as the
# Windows scheduled task "MarocLIS - Sauvegarde", run as SYSTEM every 15 minutes. At each pass it:
#
#   1. writes the installer's initial values into the database settings (cfg.Settings, Backup.*)
#      when they are missing - never over a value the laboratory changed in MarocLIS;
#   2. records that it ran (Backup.State.AgentSeenAt), which Parametres > Sauvegardes shows;
#   3. backs up when a slot of the schedule is due (Backup.Enabled, Backup.Times, Backup.Days),
#      WITH CHECKSUM, COMPRESSION when the edition has it, then RESTORE VERIFYONLY;
#   4. verifies the backups taken from a workstation, whose login may back up but not verify;
#   5. deletes the backups older than Backup.RetentionDays, except the most recent verified one.
#
# The schedule is read from the database so that it is changed from any administrator's
# workstation, which needs no right on Windows' scheduled tasks. The rules (due slot, retention,
# file names) are those of BackupSchedule, BackupRetention and BackupNaming in
# src/MarocLIS.Application/Administration/BackupSettings.cs: a change to one is a change to both.
#
# Run it by hand to back up at once, whatever the schedule:
#
#   .\maroclis-backup.ps1 -Now
#
# Logs: logs\maroclis-backup-<yyyyMM>.log next to the script. Exit code 0 when nothing failed.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI. Accented text
# written to the database is built from character codes for the same reason.

[CmdletBinding()]
param(
    [string] $Server = 'localhost\SQLEXPRESS',
    [string] $Database = 'MarocLIS',
    # Tests only: replaces the Integrated Security connection built from -Server and -Database.
    [string] $ConnectionString,
    [string] $DefaultFolder = 'C:\MarocLIS\Sauvegardes',
    [string] $DefaultTimes = '22:00',
    [int] $DefaultRetentionDays = 14,
    [string] $DefaultEnabled = 'true',
    [string] $LogFolder,
    [switch] $Now
)

$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$e = [char]0x00E9
$script:failed = $false

if (-not $LogFolder) { $LogFolder = Join-Path $PSScriptRoot 'logs' }
# "E:" on the task's command line is the root of E:; a trailing "\" there would escape the quote.
if ($DefaultFolder -match '^[A-Za-z]:$') { $DefaultFolder += '\' }

function Write-Log([string] $message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $message
    Write-Host $line
    try {
        if (-not (Test-Path $LogFolder)) { New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null }
        Add-Content -Path (Join-Path $LogFolder ('maroclis-backup-{0:yyyyMM}.log' -f (Get-Date))) -Value $line -Encoding UTF8
    }
    catch { }
}

# --- SQL ----------------------------------------------------------------------------------------

function Open-Database([string] $catalog) {
    if ($ConnectionString) {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
    }
    else {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source'] = $Server
        $builder['Integrated Security'] = $true
    }
    $builder['Initial Catalog'] = $catalog
    $builder['Connect Timeout'] = 30
    $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
    $connection.Open()
    return $connection
}

function New-Command([string] $sql, [hashtable] $parameters, [int] $timeout = 60) {
    $command = $script:connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = $timeout
    if ($parameters) {
        foreach ($name in $parameters.Keys) {
            $value = $parameters[$name]
            if ($null -eq $value) { $value = [DBNull]::Value }
            [void] $command.Parameters.AddWithValue($name, $value)
        }
    }
    return $command
}

function Invoke-NonQuery([string] $sql, [hashtable] $parameters, [int] $timeout = 60) {
    $command = New-Command $sql $parameters $timeout
    try { [void] $command.ExecuteNonQuery() } finally { $command.Dispose() }
}

function Invoke-Scalar([string] $sql, [hashtable] $parameters) {
    $command = New-Command $sql $parameters
    try { return $command.ExecuteScalar() } finally { $command.Dispose() }
}

function Get-QuotedName([string] $name) { return '[' + $name.Replace(']', ']]') + ']' }

# The resource key MarocLIS shows for an SQL Server error (SqlBackupCommands.ErrorKey).
function Get-ErrorKey($exception) {
    $inner = $exception
    while ($inner -and -not ($inner -is [System.Data.SqlClient.SqlException])) { $inner = $inner.InnerException }
    if (-not $inner) { return 'Backup_SqlError' }
    foreach ($sqlError in $inner.Errors) {
        switch ($sqlError.Number) {
            { $_ -in 3201, 15240, 3202, 3266 } { return 'Backup_FolderNotWritable' }
            { $_ -in 262, 229, 230, 3110, 3127, 916, 5011 } { return 'Backup_PermissionDenied' }
            { $_ -in 3241, 3242, 3203, 3183, 3189 } { return 'Backup_FileUnreadable' }
        }
    }
    return 'Backup_SqlError'
}

# --- Settings -----------------------------------------------------------------------------------

function Get-Values {
    $command = New-Command "SELECT [Key], [Value] FROM cfg.Settings WHERE SiteId IS NULL AND LEFT([Key], 7) = N'Backup.';" @{}
    $reader = $command.ExecuteReader()
    $values = @{}
    try {
        while ($reader.Read()) {
            $values[$reader.GetString(0)] = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
        }
    }
    finally { $reader.Dispose(); $command.Dispose() }
    return $values
}

# Writes a setting outside MarocLIS's audit trail, as the application's backup engine does for its
# state: a heartbeat every 15 minutes is not a change anybody made.
function Set-Value([string] $key, [string] $value, [string] $description) {
    Invoke-NonQuery @"
UPDATE cfg.Settings SET [Value] = @value, ModifiedAt = SYSDATETIMEOFFSET() WHERE [Key] = @key AND SiteId IS NULL;
IF @@ROWCOUNT = 0
    INSERT INTO cfg.Settings (Id, [Key], [Value], SiteId, Description, IsSecuritySensitive, CreatedAt)
    VALUES (NEWID(), @key, @value, NULL, @description, 0, SYSDATETIMEOFFSET());
"@ @{ '@key' = $key; '@value' = $value; '@description' = $description }
}

# The installer's values, written only where the laboratory has none.
function Initialize-Defaults([hashtable] $values) {
    $description = "Sauvegarde de la base (valeur initiale de l'installation, Param$([char]0x00E8)tres > Sauvegardes)"
    $defaults = [ordered] @{
        'Backup.Enabled'       = $DefaultEnabled.ToLowerInvariant()
        'Backup.Folder'        = $DefaultFolder
        'Backup.RetentionDays' = [string] $DefaultRetentionDays
        'Backup.Times'         = $DefaultTimes
        'Backup.Days'          = ''
    }
    foreach ($key in $defaults.Keys) {
        if (-not $values.ContainsKey($key)) {
            Set-Value $key $defaults[$key] $description
            $values[$key] = $defaults[$key]
            Write-Log "Setting $key initialised to '$($defaults[$key])'."
        }
    }
}

function Get-Text([hashtable] $values, [string] $key) {
    if ($values.ContainsKey($key) -and $values[$key] -and $values[$key].Trim()) { return $values[$key].Trim() }
    return $null
}

# BackupSettings.FromValues: a value that cannot be read keeps its default.
function Read-Settings([hashtable] $values) {
    $settings = @{ Enabled = $true; Folder = 'C:\MarocLIS\Sauvegardes'; RetentionDays = 14; Times = @([TimeSpan]::FromHours(22)); Days = @() }

    $enabled = Get-Text $values 'Backup.Enabled'
    if ($enabled -eq 'true') { $settings.Enabled = $true } elseif ($enabled -eq 'false') { $settings.Enabled = $false }

    $folder = Get-Text $values 'Backup.Folder'
    if ($folder) { $settings.Folder = $folder }

    $retention = 0
    $text = Get-Text $values 'Backup.RetentionDays'
    if ($text -and [int]::TryParse($text, [ref] $retention) -and $retention -ge 1 -and $retention -le 365) {
        $settings.RetentionDays = $retention
    }

    $text = Get-Text $values 'Backup.Times'
    if ($text) {
        $times = @()
        foreach ($part in ($text -split '[,; ]' | Where-Object { $_ })) {
            $time = [datetime]::MinValue
            if ([datetime]::TryParseExact($part.Trim(), [string[]] @('H:mm', 'HH:mm'), $invariant,
                                          [Globalization.DateTimeStyles]::None, [ref] $time)) {
                $times += New-Object TimeSpan($time.Hour, $time.Minute, 0)
            }
        }
        if ($times.Count -gt 0) { $settings.Times = @($times | Sort-Object -Unique) }
    }

    $text = Get-Text $values 'Backup.Days'
    if ($text) {
        $days = @()
        foreach ($part in ($text -split '[,; ]' | Where-Object { $_ })) {
            $day = [DayOfWeek]::Sunday
            if ($part -notmatch '^\d+$' -and [Enum]::TryParse([DayOfWeek], $part.Trim(), $true, [ref] $day)) { $days += $day }
        }
        $settings.Days = @($days | Sort-Object -Unique)
    }

    return $settings
}

# BackupSchedule.DueSlot: the latest slot of the last 24 hours later than the last one handled.
function Get-DueSlot($settings, $lastSlot, [datetime] $now) {
    if (-not $settings.Enabled) { return $null }
    $from = $now.AddHours(-24)
    if ($lastSlot -and $lastSlot -gt $from) { $from = $lastSlot }

    $due = $null
    for ($day = $from.Date; $day -le $now.Date; $day = $day.AddDays(1)) {
        if ($settings.Days.Count -gt 0 -and -not ($settings.Days -contains $day.DayOfWeek)) { continue }
        foreach ($time in $settings.Times) {
            $slot = $day.Add($time)
            if ($slot -gt $from -and $slot -le $now) { $due = $slot }
        }
    }
    return $due
}

function Format-Moment([datetimeoffset] $at) { return $at.ToString('yyyy-MM-ddTHH:mm:sszzz', $invariant) }

function Get-SafeName([string] $name) { return ($name -replace '[^A-Za-z0-9_-]', '_') }

# BackupNaming.Combine: the folder's own separator.
function Join-BackupPath([string] $folder, [string] $file) {
    $separator = '\'
    if ($folder.Contains('/') -and -not $folder.Contains('\')) { $separator = '/' }
    return $folder.Trim().TrimEnd('\', '/') + $separator + $file
}

# --- Security log -------------------------------------------------------------------------------

# The same rows MarocLIS writes (aud.SecurityEvents), so a scheduled backup shows in the log beside
# the manual ones. Kinds: 30 BackupCompleted, 31 BackupFailed (SecurityEventKind).
function Write-SecurityEvent([int] $kind, [bool] $succeeded, [string] $details, [string] $reason) {
    try {
        if ($details.Length -gt 2000) { $details = $details.Substring(0, 2000) }
        Invoke-NonQuery @"
INSERT INTO aud.SecurityEvents (Id, Kind, OccurredAt, Succeeded, AttemptedUserName, Workstation, OsUser, FailureReasonKey, Details)
VALUES (NEWID(), @kind, SYSDATETIMEOFFSET(), @succeeded, @user, @workstation, @osUser, @reason, @details);
"@ @{ '@kind' = $kind; '@succeeded' = $succeeded; '@user' = ('t' + [char]0x00E2 + "che planifi${e}e");
      '@workstation' = [Environment]::MachineName; '@osUser' = [Environment]::UserName; '@reason' = $reason; '@details' = $details }
    }
    catch { Write-Log "Security log not written: $($_.Exception.Message)" }
}

# --- Folder -------------------------------------------------------------------------------------

# A local folder is created, and SQL Server's service account given the right to write in it: it is
# the service, not this script, that writes the backup. A share cannot be fixed from here; its
# permissions must admit the server's computer account (DOMAIN\SERVER$), which is how a service
# account reaches the network.
function Initialize-Folder([string] $folder, [hashtable] $values) {
    if (-not $onWindows) { return }
    if ($folder -notmatch '^[A-Za-z]:\\') { return }

    if (-not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Write-Log "Folder $folder created."
    }

    if ((Get-Text $values 'Backup.State.GrantedFolder') -eq $folder) { return }

    # The service of the instance in -Server (SQLEXPRESS in localhost\SQLEXPRESS); its account as
    # Windows records it, "NT Service\MSSQL$SQLEXPRESS" for the virtual account SQL Server's setup uses.
    $instance = if ($Server.Contains('\')) { $Server.Substring($Server.IndexOf('\') + 1) } else { 'MSSQLSERVER' }
    $name = if ($instance -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { 'MSSQL$' + $instance }
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name = '" + $name + "'") -ErrorAction SilentlyContinue
    $account = if ($service) { $service.StartName } else { $null }
    if (-not $account -or $account -eq 'LocalSystem') { return }

    & icacls.exe $folder /grant "${account}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Log "SQL Server service account $account may write into $folder."
        Set-Value 'Backup.State.GrantedFolder' $folder "Dossier ouvert au compte du service SQL Server"
    }
    else {
        Write-Log "icacls could not grant $account on ${folder}: exit code $LASTEXITCODE."
    }
}

# --- Backup -------------------------------------------------------------------------------------

function Invoke-Backup([string] $folder, $info, [datetime] $at) {
    $file = (Get-SafeName $Database) + '_' + $at.ToString('yyyyMMdd_HHmmss', $invariant) + '.bak'
    $path = Join-BackupPath $folder $file

    $sql = 'BACKUP DATABASE ' + (Get-QuotedName $Database) +
           ' TO DISK = @path WITH CHECKSUM, FORMAT, INIT, NAME = @name, DESCRIPTION = @description'
    if ($info.Compress) { $sql += ', COMPRESSION' }

    $description = 'T' + [char]0x00E2 + "che planifi${e}e du serveur " + [Environment]::MachineName
    Write-Log "Backing up $Database to $path$(if ($info.Compress) { ' (compressed)' })."
    Invoke-NonQuery $sql @{ '@path' = $path; '@name' = 'MarocLIS:scheduled'; '@description' = $description } 10800
    return $path
}

function Test-Backup([string] $path) {
    Invoke-NonQuery 'RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;' @{ '@path' = $path } 10800
}

# --- Retention ----------------------------------------------------------------------------------

# BackupRetention.Expired: older than the retention, except the most recent verified backup.
function Remove-Expired([string] $folder, [int] $retentionDays, [hashtable] $values, [datetime] $now) {
    if (-not (Test-Path -LiteralPath $folder)) { return }

    $prefix = (Get-SafeName $Database) + '_'
    $backups = @()
    foreach ($item in Get-ChildItem -LiteralPath $folder -File -Filter '*.bak') {
        if (-not $item.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $stamp = if ($item.Name.Length -ge $prefix.Length + 15) { $item.Name.Substring($prefix.Length, 15) } else { '' }
        $taken = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($stamp, 'yyyyMMdd_HHmmss', $invariant, [Globalization.DateTimeStyles]::None, [ref] $taken)) {
            continue
        }
        $check = Get-Text $values ('Backup.Check.' + $item.Name)
        $backups += [pscustomobject] @{ Item = $item; Taken = $taken; Verified = ($check -and $check.StartsWith('ok;')) }
    }

    $keep = $backups | Where-Object { $_.Verified } | Sort-Object Taken -Descending | Select-Object -First 1
    $cutoff = $now.AddDays(-$retentionDays)

    foreach ($backup in ($backups | Where-Object { $_.Taken -lt $cutoff } | Sort-Object Taken)) {
        if ($keep -and $backup.Item.FullName -eq $keep.Item.FullName) { continue }
        try {
            Remove-Item -LiteralPath $backup.Item.FullName -Force
            Set-Value ('Backup.Check.' + $backup.Item.Name) ('deleted;' + (Format-Moment ([datetimeoffset]::Now))) "Supprim${e}e par la conservation"
            Write-Log "Deleted $($backup.Item.FullName) (older than $retentionDays days)."
        }
        catch { Write-Log "Could not delete $($backup.Item.FullName): $($_.Exception.Message)" }
    }

    # The files SQL Server wrote to test the folder (Parametres > Sauvegardes, "Tester").
    foreach ($probe in Get-ChildItem -LiteralPath $folder -File -Filter 'MarocLIS-test-ecriture-*.cer') {
        try { Remove-Item -LiteralPath $probe.FullName -Force } catch { }
    }
}

# --- Main ---------------------------------------------------------------------------------------

try {
    $script:connection = Open-Database 'master'
    $exists = Invoke-Scalar 'SELECT DB_ID(@db);' @{ '@db' = $Database }
    $script:connection.Dispose()
    if ($null -eq $exists -or $exists -is [DBNull]) {
        Write-Log "Database $Database not found on $Server; nothing to do."
        exit 0
    }

    $script:connection = Open-Database $Database
    if ((Invoke-Scalar "SELECT OBJECT_ID(N'cfg.Settings');" @{}) -is [DBNull]) {
        # Before MarocLIS's first start: the schema, and so the data, do not exist yet.
        Write-Log 'The MarocLIS schema is not created yet (MarocLIS has not been started); nothing to back up.'
        exit 0
    }

    $values = Get-Values
    Initialize-Defaults $values
    Set-Value 'Backup.State.AgentSeenAt' (Format-Moment ([datetimeoffset]::Now)) "Dernier passage de la t$([char]0x00E2)che planifi${e}e"
    Set-Value 'Backup.State.AgentHost' ([Environment]::MachineName) "Serveur de la t$([char]0x00E2)che planifi${e}e"

    $settings = Read-Settings $values
    $serverNow = [datetime] (Invoke-Scalar 'SELECT GETDATE();' @{})
    $edition = [int] (Invoke-Scalar "SELECT CONVERT(int, SERVERPROPERTY('EngineEdition'));" @{})
    $info = @{ Compress = ($edition -in 2, 3, 8) }

    $lastSlot = $null
    $text = Get-Text $values 'Backup.State.LastSlot'
    $parsed = [datetime]::MinValue
    if ($text -and [datetime]::TryParseExact($text, 'yyyy-MM-ddTHH:mm', $invariant, [Globalization.DateTimeStyles]::None, [ref] $parsed)) {
        $lastSlot = $parsed
    }

    $slot = Get-DueSlot $settings $lastSlot $serverNow
    if ($slot -or $Now) {
        # Recorded first: a slot whose backup fails is reported, not retried every 15 minutes.
        if ($slot) { Set-Value 'Backup.State.LastSlot' $slot.ToString('yyyy-MM-ddTHH:mm', $invariant) "Dernier horaire de sauvegarde trait${e}" }

        $path = $null
        try {
            Initialize-Folder $settings.Folder $values
            $path = Invoke-Backup $settings.Folder $info $serverNow
            $file = Split-Path -Leaf $path
            if ($path.Contains('/') -and -not $path.Contains('\')) { $file = $path.Substring($path.LastIndexOf('/') + 1) }

            try {
                Test-Backup $path
                Set-Value ('Backup.Check.' + $file) ('ok;' + (Format-Moment ([datetimeoffset]::Now))) "V${e}rification de la sauvegarde"
                Set-Value 'Backup.State.LastSuccessAt' (Format-Moment ([datetimeoffset]::Now)) "Derni$([char]0x00E8)re sauvegarde r${e}ussie"
                Set-Value 'Backup.State.LastResult' ('ok;' + (Format-Moment ([datetimeoffset]::Now)) + ';' + $file) "R${e}sultat de la derni$([char]0x00E8)re sauvegarde"
                Write-SecurityEvent 30 $true "MarocLIS:scheduled $path (v${e}rifi${e}e)" $null
                Write-Log "Backup $path written and verified."
                $values[('Backup.Check.' + $file)] = 'ok;'
            }
            catch {
                $key = Get-ErrorKey $_.Exception
                Set-Value ('Backup.Check.' + $file) ('failed;' + (Format-Moment ([datetimeoffset]::Now)) + ';' + $key) "V${e}rification de la sauvegarde"
                Set-Value 'Backup.State.LastResult' ('failed;' + (Format-Moment ([datetimeoffset]::Now)) + ';Backup_VerifyFailed') "R${e}sultat de la derni$([char]0x00E8)re sauvegarde"
                Write-SecurityEvent 31 $false "$path : $($_.Exception.Message)" 'Backup_VerifyFailed'
                Write-Log "FAILED: verification of $path - $($_.Exception.Message)"
                $script:failed = $true
            }
        }
        catch {
            $key = Get-ErrorKey $_.Exception
            Set-Value 'Backup.State.LastResult' ('failed;' + (Format-Moment ([datetimeoffset]::Now)) + ';' + $key) "R${e}sultat de la derni$([char]0x00E8)re sauvegarde"
            Write-SecurityEvent 31 $false "$($settings.Folder) : $($_.Exception.Message)" $key
            Write-Log "FAILED: backup into $($settings.Folder) - $($_.Exception.Message)"
            $script:failed = $true
        }
    }

    # The backups a workstation wrote and could not verify.
    foreach ($key in @($values.Keys | Where-Object { $_.StartsWith('Backup.Check.') })) {
        $value = $values[$key]
        if (-not $value -or -not $value.StartsWith('pending')) { continue }
        $file = $key.Substring('Backup.Check.'.Length)
        $path = Join-BackupPath $settings.Folder $file
        $exists = $true
        if ($onWindows -or -not $path.Contains('\')) { $exists = Test-Path -LiteralPath $path }
        if (-not $exists) { continue }
        try {
            Test-Backup $path
            Set-Value $key ('ok;' + (Format-Moment ([datetimeoffset]::Now))) "V${e}rification de la sauvegarde"
            $values[$key] = 'ok;'
            Write-Log "Backup $path verified."
        }
        catch {
            Set-Value $key ('failed;' + (Format-Moment ([datetimeoffset]::Now)) + ';' + (Get-ErrorKey $_.Exception)) "V${e}rification de la sauvegarde"
            Write-SecurityEvent 31 $false "$path : $($_.Exception.Message)" 'Backup_VerifyFailed'
            Write-Log "FAILED: verification of $path - $($_.Exception.Message)"
            $script:failed = $true
        }
    }

    Remove-Expired $settings.Folder $settings.RetentionDays $values $serverNow
}
catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($script:connection) { $script:connection.Dispose() }
}

if ($script:failed) { exit 1 }
exit 0
