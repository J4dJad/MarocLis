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
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI.

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Iscc,
    [switch] $SkipPublish
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
& $Iscc "/DAppVersion=$version" "/DStagingDir=$staging" "/DRepoRoot=$repoRoot" "/O$output" (Join-Path $installerDir 'MarocLIS.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $output "MarocLIS-Setup-$version.exe"
Write-Host ''
Write-Host "Setup program: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host 'To install SQL Server Express without Internet on the server, put SQLEXPR_x64_ENU.exe next to it.'
