# Builds the MarocLIS setup program: installer\output\MarocLIS-Setup-<version>.exe.
#
#   .\installer\build-installer.ps1                      # publish, then compile
#   .\installer\build-installer.ps1 -SkipPublish         # compile the staging folder as it is
#   .\installer\build-installer.ps1 -Iscc 'D:\Tools\Inno Setup 6\ISCC.exe'
#   .\installer\build-installer.ps1 -FirefoxVersion 115.30.0 -FirefoxSha256 <sha256>   # pin the browser
#
# Needs the .NET 10 SDK and Inno Setup 6.3 or later in the 6.x line (https://jrsoftware.org/isdl.php;
# ISCC.exe is looked for in -Iscc, then the INNO_SETUP_ISCC environment variable, then the PATH,
# then the usual install folders). Inno Setup 6 (6.7.3 tested) builds a setup.exe that runs on
# Windows 7 SP1 and later, which the "Poste Web" type needs; the script refuses a 7.x or later
# ISCC, whose Windows floor has not been checked.
#
# The Desktop, the Api and the Web are published self-contained for win-x64, so neither the
# laboratory server nor the Windows workstations need a .NET runtime. The Api publish also publishes the phone
# application (MarocLIS.Mobile, Blazor WebAssembly) into its wwwroot, and both carry the report
# fonts (Assets\Fonts, from MarocLIS.Reporting) and the QuestPDF / SkiaSharp native libraries.
# The script checks those files are there before packaging, because a setup.exe missing one of
# them installs cleanly and then fails at the first report or the first phone sign-in.
#
# The Firefox ESR 115 installer. The "Poste Web" type installs it on a workstation whose browsers
# are all too old for MarocLIS Web (Windows 7, 8 and 8.1 get no Chrome or Edge after 109, no
# Firefox after ESR 115). The script fetches Mozilla's own offline installer, French, 32-bit (it runs
# on 32- and 64-bit Windows), into installer\prereqs, once: the newest 115.x ESR listed on
# https://ftp.mozilla.org/pub/firefox/releases/, or -FirefoxVersion. Every build checks it: its
# Authenticode signature must be valid and Mozilla Corporation's (Windows only), and its SHA-256
# must be -FirefoxSha256 when given (required where Authenticode cannot be checked). A file put
# there by hand (Firefox-Setup-esr115-fr-win32.exe) is checked the same way.
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
    [switch] $AllowDevelopmentKey,
    # A 115.x ESR version (115.30.0); the newest one when left out.
    [string] $FirefoxVersion,
    # The installer's expected SHA-256 (hex); required when Authenticode cannot be checked.
    [string] $FirefoxSha256
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
    Invoke-Publish 'src/MarocLIS.Web/MarocLIS.Web.csproj' (Join-Path $staging 'Web')
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
    'Web'     = @('MarocLIS.Web.exe', 'hostfxr.dll', 'appsettings.json', 'Resources/fr.json',
                  'Assets/Fonts/Amiri-Regular.ttf', 'Assets/Fonts/Carlito-Regular.ttf',
                  'libSkiaSharp.dll', 'QuestPdfSkia.dll', 'Microsoft.Data.SqlClient.SNI.dll',
                  'MarocLIS.Web.staticwebassets.endpoints.json', 'wwwroot/css/app.css', 'wwwroot/js/compat.js',
                  'wwwroot/js/maroclis.js', 'wwwroot/_framework/blazor.web.js', 'wwwroot/icons/FlaskConical.png')
}
foreach ($folder in $required.Keys) {
    foreach ($item in $required[$folder]) {
        $path = Join-Path (Join-Path $staging $folder) $item
        if (-not (Test-Path $path)) { throw "Missing from the published files: $folder/$item" }
    }
}
Write-Host 'Published files checked.'

# --- Firefox ESR 115 for the "Poste Web" type -----------------------------------------------------

$prereqs = Join-Path $installerDir 'prereqs'
$firefoxFile = Join-Path $prereqs 'Firefox-Setup-esr115-fr-win32.exe'
$firefoxVersionFile = Join-Path $prereqs 'Firefox-Setup-esr115-fr-win32.version.txt'
$mozillaReleases = 'https://ftp.mozilla.org/pub/firefox/releases/'

function Get-LatestFirefoxEsr115 {
    $listing = (Invoke-WebRequest -Uri $mozillaReleases -UseBasicParsing).Content
    $versions = [regex]::Matches($listing, '/(115\.\d+\.\d+)esr/') | ForEach-Object { [version] $_.Groups[1].Value } |
        Sort-Object -Unique
    if (-not $versions) { throw "No 115.x ESR release found on $mozillaReleases." }
    return ($versions | Select-Object -Last 1).ToString()
}

if (-not (Test-Path $firefoxFile)) {
    if (-not $FirefoxVersion) { $FirefoxVersion = Get-LatestFirefoxEsr115 }
    if ($FirefoxVersion -notmatch '^115\.\d+\.\d+$') { throw "-FirefoxVersion must be a 115.x.y version (got '$FirefoxVersion')." }
    $url = "${mozillaReleases}${FirefoxVersion}esr/win32/fr/Firefox%20Setup%20${FirefoxVersion}esr.exe"
    Write-Host "Downloading Firefox ESR $FirefoxVersion (fr, win32): $url"
    New-Item -ItemType Directory -Path $prereqs -Force | Out-Null
    $partial = "$firefoxFile.part"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    }
    catch {
        Remove-Item $partial -ErrorAction SilentlyContinue
        throw "Firefox ESR $FirefoxVersion could not be downloaded from $url ($($_.Exception.Message)). Download it by hand and save it as $firefoxFile."
    }
    Move-Item $partial $firefoxFile -Force
    Set-Content -Path $firefoxVersionFile -Value $FirefoxVersion -Encoding ASCII
}
if (-not (Test-Path $firefoxFile)) {
    throw "Firefox ESR 115 installer missing: $firefoxFile. The Poste Web type cannot be built without it."
}

# Its signature: Mozilla's, valid. Where Authenticode cannot be checked (PowerShell outside Windows),
# the pinned hash is the only proof and is then required.
$firefoxHash = (Get-FileHash -Path $firefoxFile -Algorithm SHA256).Hash
if ($FirefoxSha256 -and $firefoxHash -ne $FirefoxSha256.Trim().ToUpperInvariant()) {
    throw "Firefox installer $firefoxFile has SHA-256 $firefoxHash, not the expected $FirefoxSha256. Delete it and build again."
}
if (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue) {
    $signature = Get-AuthenticodeSignature -FilePath $firefoxFile
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
    if ($signature.Status -ne 'Valid' -or $signer -notmatch '(^|,\s*)O="?Mozilla Corporation"?(,|$)') {
        throw "Firefox installer $firefoxFile is not validly signed by Mozilla Corporation (status $($signature.Status), signer '$signer'). Delete it and build again."
    }
    Write-Host "Firefox installer signed by Mozilla Corporation; SHA-256 $firefoxHash."
}
elseif (-not $FirefoxSha256) {
    throw "Authenticode cannot be checked here: pass -FirefoxSha256 with the installer's SHA-256 ($firefoxHash) once compared with Mozilla's SHA256SUMS."
}
else {
    Write-Host "Firefox installer SHA-256 matches the pinned $firefoxHash."
}

$firefoxFileVersion = if (Test-Path $firefoxVersionFile) { (Get-Content $firefoxVersionFile -TotalCount 1).Trim() } else { '' }
if (-not $firefoxFileVersion) { $firefoxFileVersion = (Get-Item $firefoxFile).VersionInfo.ProductVersion }
if (-not $firefoxFileVersion) { $firefoxFileVersion = '115 ESR' }
Write-Host "Firefox ESR bundled: $firefoxFileVersion"

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

# Inno Setup 6.3 or later (the x86compatible architecture, IsX64Compatible), still 6.x: the 6.x line
# runs on Windows 7 SP1 (its MinVersion floor, 6.1sp1), which the "Poste Web" type needs.
if ($Iscc -like '*.exe') {
    $isccVersion = [version] ((Get-Item $Iscc).VersionInfo.FileVersion -replace '[^0-9.].*$', '')
    if ($isccVersion -lt [version] '6.3' -or $isccVersion.Major -ne 6) {
        throw "Inno Setup $isccVersion found ($Iscc): use Inno Setup 6.3 or later 6.x (6.7.3 tested); its setup.exe runs on Windows 7 SP1, a later major version's may not."
    }
    Write-Host "Inno Setup $isccVersion"
}

Write-Host "Compiling with $Iscc"
& $Iscc "/DAppVersion=$version" "/DStagingDir=$staging" "/DRepoRoot=$repoRoot" "/DFirefoxSetupFile=$firefoxFile" `
    "/DFirefoxVersion=$firefoxFileVersion" "/O$output" "/F$outputName" (Join-Path $installerDir 'MarocLIS.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $output "$outputName.exe"
Write-Host ''
Write-Host "Setup program: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host 'To install SQL Server Express without Internet on the server, put SQLEXPR_x64_ENU.exe next to it.'
