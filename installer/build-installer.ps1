# Builds the MarocLIS setup program: installer\output\MarocLIS-Setup-<version>.exe.
#
#   .\installer\build-installer.ps1                      # publish, then compile
#   .\installer\build-installer.ps1 -SkipPublish         # compile the staging folder as it is
#   .\installer\build-installer.ps1 -Iscc 'D:\Tools\Inno Setup 6\ISCC.exe'
#
# Needs the .NET 10 SDK and Inno Setup 6 (https://jrsoftware.org/isdl.php; ISCC.exe is looked for
# in -Iscc, then the INNO_SETUP_ISCC environment variable, then the PATH, then the usual install
# folders).
#
# The Desktop and the Api are published self-contained for win-x64, so neither the laboratory
# server nor the workstations need a .NET runtime. The Api publish also publishes the phone
# application (MarocLIS.Mobile, Blazor WebAssembly) into its wwwroot, and both carry the report
# fonts (Assets\Fonts, from MarocLIS.Reporting) and the QuestPDF / SkiaSharp native libraries.
# The script checks those files are there before packaging, because a setup.exe missing one of
# them installs cleanly and then fails at the first report or the first phone sign-in.
#
# Paths are written with '/' so the script also runs under PowerShell 7 elsewhere than Windows
# (with -Iscc naming a wrapper that runs ISCC.exe), which is how it was tested.
#
# The licence key: MarocLIS verifies licences with the public key compiled into it
# (src/MarocLIS.Application/Licensing/licence-public-key.pem). The repository ships a DEVELOPMENT key
# whose private half is in tests/, so anyone with the source could sign licences for a build that
# embeds it. The script refuses to build a setup program with that key; -AllowDevelopmentKey builds
# one anyway for internal testing, named ...-CLE-DEV.exe so it cannot be mistaken for a release.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI.

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Iscc,
    [switch] $SkipPublish,
    [switch] $AllowDevelopmentKey
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $installerDir '..')).Path
$staging = Join-Path (Join-Path $installerDir 'build') 'staging'
$output = Join-Path $installerDir 'output'

# The product version, from Directory.Build.props (<Version>).
[xml] $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
Write-Host "MarocLIS $version"

# The licence key's fingerprint, computed as MarocLIS and the licence generator compute it: the first
# 16 bytes of the SHA-256 of the key's SubjectPublicKeyInfo, in groups of four hex digits.
$developmentFingerprint = 'CDC1-D2A2-222A-4A92-B835-95FB-909A-18BB'
$keyFile = Join-Path $repoRoot 'src/MarocLIS.Application/Licensing/licence-public-key.pem'
$keyText = Get-Content $keyFile -Raw
if ($keyText -notmatch '-----BEGIN PUBLIC KEY-----([A-Za-z0-9+/=\s]+)-----END PUBLIC KEY-----') {
    throw "No public key block in $keyFile."
}
$spki = [Convert]::FromBase64String(($Matches[1] -replace '\s', ''))
$sha = [System.Security.Cryptography.SHA256]::Create()
$hex = -join ($sha.ComputeHash($spki)[0..15] | ForEach-Object { $_.ToString('X2') })
$fingerprint = (0..7 | ForEach-Object { $hex.Substring($_ * 4, 4) }) -join '-'
Write-Host "Licence key fingerprint: $fingerprint"

$outputName = "MarocLIS-Setup-$version"
if ($fingerprint -eq $developmentFingerprint) {
    if (-not $AllowDevelopmentKey) {
        throw @"
MarocLIS embeds the DEVELOPMENT licence key ($fingerprint): its private key is in the repository
(tests/MarocLIS.Tests/Licensing), so anyone could sign licences that this build accepts.
Replace the key before building a release:
  1. In the Licence Generator (tools/MarocLIS.LicenseGenerator), on the vendor's computer:
     'Cle de signature' > 'Exporter la cle publique...' (create the key first if needed).
  2. Copy the exported licence-public-key.pem over
     src/MarocLIS.Application/Licensing/licence-public-key.pem and commit it.
  3. Run this script again. See docs/guide-installation.md, part 2 (the key ceremony).
For an internal test setup only, -AllowDevelopmentKey builds $outputName-CLE-DEV.exe.
"@
    }
    Write-Warning 'Building with the DEVELOPMENT licence key: internal testing only, never for a laboratory.'
    $outputName = "$outputName-CLE-DEV"
}

function Invoke-Publish([string] $project, [string] $destination) {
    Write-Host "Publishing $project -> $destination"
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    & dotnet publish (Join-Path $repoRoot $project) -c $Configuration -r win-x64 --self-contained true `
        -p:EnableWindowsTargeting=true -o $destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed (exit code $LASTEXITCODE)." }
    # Development settings are never shipped: they would override the laboratory's own.
    Remove-Item (Join-Path $destination 'appsettings.Development.json') -ErrorAction SilentlyContinue
}

if (-not $SkipPublish) {
    Invoke-Publish 'src/MarocLIS.Desktop/MarocLIS.Desktop.csproj' (Join-Path $staging 'Desktop')
    Invoke-Publish 'src/MarocLIS.Api/MarocLIS.Api.csproj' (Join-Path $staging 'Api')
}

# What a working installation needs, per folder.
$required = @{
    'Desktop' = @('MarocLIS.exe', 'hostfxr.dll', 'appsettings.json', 'Resources/fr.json', 'Help',
                  'Assets/Fonts/Amiri-Regular.ttf', 'Assets/Fonts/Carlito-Regular.ttf',
                  'libSkiaSharp.dll', 'QuestPdfSkia.dll', 'Microsoft.Data.SqlClient.SNI.dll')
    'Api'     = @('MarocLIS.Api.exe', 'hostfxr.dll', 'appsettings.json', 'Resources/fr.json',
                  'Assets/Fonts/Amiri-Regular.ttf', 'Assets/Fonts/Carlito-Regular.ttf',
                  'libSkiaSharp.dll', 'QuestPdfSkia.dll', 'Microsoft.Data.SqlClient.SNI.dll',
                  'wwwroot/index.html', 'wwwroot/_framework/dotnet.js', 'wwwroot/_framework/dotnet.native.wasm',
                  'wwwroot/_framework/MarocLIS.Mobile.wasm', 'wwwroot/service-worker-assets.js')
}
foreach ($folder in $required.Keys) {
    foreach ($item in $required[$folder]) {
        $path = Join-Path (Join-Path $staging $folder) $item
        if (-not (Test-Path $path)) { throw "Missing from the published files: $folder/$item" }
    }
}
Write-Host 'Published files checked.'

if (-not $Iscc) { $Iscc = $env:INNO_SETUP_ISCC }
if (-not $Iscc) {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $Iscc = $onPath.Source }
}
if (-not $Iscc) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw 'ISCC.exe (Inno Setup 6) not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or pass -Iscc.'
}

Write-Host "Compiling with $Iscc"
& $Iscc "/DAppVersion=$version" "/DStagingDir=$staging" "/DRepoRoot=$repoRoot" "/O$output" "/F$outputName" (Join-Path $installerDir 'MarocLIS.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $output "$outputName.exe"
Write-Host ''
Write-Host "Setup program: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host 'To install SQL Server Express without Internet on the server, put SQLEXPR_x64_ENU.exe next to it.'
