# Reachability and wiring verification for MarocLIS.
#
# Runs without an SDK, like check-resource-keys.ps1, and answers the question that produced most of
# the defects in this repository's history: is there a path from the user interface to the code that
# was written? A finished service method with no caller is not a finished feature — it is a feature
# that looks done in a review and does nothing in a laboratory.
#
# Five checks:
#   1. Every public method on a service, sink, driver or renderer has a caller outside its own file.
#   2. Every LocalizedView is registered in DI and appears in the shell's navigation.
#   3. Every query interface declared in the application layer has a registered implementation.
#   4. Every application service is registered in the container.
#   5. Every type named in a `new X(` or a static call resolves to a declaration somewhere in src.
#      This is the check that would have caught the DemoDataSeeder compile error.
#
# ASCII only, UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less file as ANSI and mangles any
# non-ASCII character in it.

[CmdletBinding()]
param(
    # Repository root. Defaults to the working directory, like check-resource-keys.ps1, rather than
    # to $PSScriptRoot: Windows PowerShell 5.1 does not populate that when evaluating a parameter
    # default, which fails before the first line of the script body runs.
    [string] $SourceDirectory = '.',
    [switch] $Detailed
)

$ErrorActionPreference = 'Stop'

$script:Failures = 0

function Write-Pass([string] $message) { Write-Host "  PASS  $message" -ForegroundColor DarkGreen }

function Write-Fail([string] $message) {
    Write-Host "  FAIL  $message" -ForegroundColor Red
    $script:Failures++
}

function Write-Note([string] $message) { Write-Host "        $message" -ForegroundColor DarkGray }

Write-Host 'Reachability verification'
Write-Host '--------------------------------'

$src = Join-Path $SourceDirectory 'src'

# Path separators are matched as [\\/] throughout: written as '\\' only, the project filters
# matched no file at all under PowerShell on Linux or macOS, and checks 1 and 4 passed there
# without checking anything ("0 application services found").
$files = Get-ChildItem -Path $src -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

$documents = @{}

foreach ($file in $files) {
    $documents[$file.FullName] = @{
        Name = $file.Name
        Text = (Get-Content $file.FullName -Raw -Encoding UTF8)
    }
}

$allText = ($documents.Values | ForEach-Object { $_.Text }) -join "`n"

# --- 1. Public service methods with no caller -----------------------------------------------------

Write-Host ''
Write-Host '1. Public methods in Application, Instruments and Reporting are called from somewhere else'

# Methods on the classes the UI and the instrument host drive. Property accessors, constructors and
# records are excluded by requiring a parameter list and an async/Task/Result return shape.
#
# Scope widened twice, and each widening found a real defect:
#   - Application\*Service.cs only: this missed InstrumentMessageSink.ReplayAsync entirely. The
#     replay path was implemented, had a permission constant, a CanReplay predicate and resource
#     keys, and no caller anywhere - so a laboratory running with auto-acceptance off could never
#     accept a held transmission. Three projects do the work in this solution, not one.
#   - *Service.cs only: the sink, the drivers and the renderers do not end in 'Service', which is
#     exactly why the gap above survived. The file pattern now follows the roles, not the suffix.
$workerProjects = '[\\/]MarocLIS\.(Application|Instruments|Reporting)[\\/]'
$workerFiles = '(Service|Sink|Renderer|Driver|Importer|Replayer)\.cs$'

$serviceFiles = $documents.GetEnumerator() |
    Where-Object { $_.Value.Name -match $workerFiles -and $_.Key -match $workerProjects }

# Check 4 keeps the narrow scope on purpose: it asks whether AddMarocLisApplication registers the
# application services, and nothing outside that layer belongs in that answer. InstrumentHostedService
# is registered by AddInstrumentInterfacing as a hosted service, which is correct and is not check 4's
# business. Sharing one file set between checks 1 and 4 made the widening above report that as a
# failure.
$applicationServiceFiles = $documents.GetEnumerator() |
    Where-Object { $_.Value.Name -match 'Service\.cs$' -and $_.Key -match '[\\/]MarocLIS\.Application[\\/]' }

# Methods that are genuinely unreachable, known to be so, and recorded as a gap in the design
# document. Listed here so the checker stays green on what it has already reported while still
# printing them every run: an allow-list that is silent is how a gap becomes permanent.
$recordedGaps = @{
    'AstmInstrumentDriver.cs :: SendOrdersAsync' =
        'host-initiated worklist download is implemented on both drivers and never triggered - docs 04 section 8'
    'Poct1AInstrumentDriver.cs :: SendOrdersAsync' =
        'IInstrumentDriver member; POCT1-A has no order download in this build (returns false), results flow one way - same host-initiated gap as ASTM'
}

$uncalled = New-Object System.Collections.Generic.List[string]

foreach ($entry in $serviceFiles) {
    $text = $entry.Value.Text
    $name = $entry.Value.Name

    # The generic argument may itself be generic. The previous pattern used Task<[^>]*>, and a
    # negated character class cannot cross the '>' it excludes - so it matched Task<bool> and
    # Result<Guid> and silently skipped every Task<Result<IReadOnlyList<Row>>>, which is the shape
    # of nearly every read method in this application. Check 1 was therefore passing on a small
    # minority of the methods it claimed to cover, and GetPatientAccessHistoryAsync was written,
    # registered, and left with no caller without this noticing.
    #
    # The inner class allows '<' and '>' and is lazy, so backtracking settles on the last '>' that
    # still leaves a method name and an open paren after it.
    $matches = [regex]::Matches(
        $text,
        'public\s+(?:async\s+)?(?:Task|ValueTask|Result)(?:<[\w\s,<>\?\.\[\]]*?>)?\s+(\w+)\s*\(')

    foreach ($match in $matches) {
        $method = $match.Groups[1].Value

        # Interface declarations and the definition itself do not count as calls.
        $callPattern = "(?<![\w.])(?:await\s+)?[\w\.\)\]]+\.$([regex]::Escape($method))\s*\("

        $callers = @($documents.GetEnumerator() |
            Where-Object { $_.Value.Name -ne $name -and $_.Value.Text -match $callPattern } |
            ForEach-Object { $_.Value.Name })

        # A call from inside its own class counts too: such a method is reached through the
        # class's other public methods, which this check verifies in turn. Leave accrual is run by
        # LeaveService's own balance and approval methods, and the POCT1-A driver feeds its parser
        # from its own read loop; both were reported as having no caller once the path filters
        # above matched on Linux. Not for an interface member: a contract method exists to be
        # called from outside, and the drivers' SendOrdersAsync, which only their own query
        # handling calls, is precisely the recorded gap below.
        if ($callers.Count -eq 0) {
            $escaped = [regex]::Escape($method)
            $contractPattern = "(?m)^\s*(?:Task|ValueTask|Result)(?:<[\w\s,<>\?\.\[\]]*?>)?\s+$escaped\s*\([^;{}]*\)\s*;"
            $declarations = [regex]::Matches($text, "public\s+[^;{}=]*?\b$escaped\s*\(").Count
            $ownCalls = [regex]::Matches($text, "(?<![\w.])(?:this\.)?$escaped\s*\(").Count - $declarations

            if ($ownCalls -gt 0 -and $allText -notmatch $contractPattern) { $callers = @($name) }
        }

        # Two classes in one file can declare the same method name - both ASTM and HL7 drivers
        # implement SendOrdersAsync - and reporting it twice says nothing the first line did not.
        $key = "$name :: $method"

        if ($callers.Count -eq 0 -and -not $uncalled.Contains($key)) { $uncalled.Add($key) }
    }
}

$genuinelyUncalled = @($uncalled | Where-Object { -not $recordedGaps.ContainsKey($_) })

foreach ($item in @($uncalled | Where-Object { $recordedGaps.ContainsKey($_) })) {
    Write-Note "GAP   $item"
    Write-Note "      $($recordedGaps[$item])"
}

if ($genuinelyUncalled.Count -eq 0) {
    Write-Pass 'every public method has a caller outside its own file, or is a recorded gap'
}
else {
    foreach ($item in $genuinelyUncalled) { Write-Fail "no caller found for $item" }
}

# --- 2. Views are registered and navigable --------------------------------------------------------

Write-Host ''
Write-Host '2. Every view is registered in DI and reachable from the shell'

$viewNames = [regex]::Matches($allText, 'class\s+(\w+View)\s*:\s*LocalizedView') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$composition = ($documents.GetEnumerator() |
    Where-Object { $_.Value.Name -eq 'DesktopServiceCollectionExtensions.cs' } |
    ForEach-Object { $_.Value.Text }) -join "`n"

$shell = ($documents.GetEnumerator() |
    Where-Object { $_.Value.Name -eq 'MainShellForm.cs' } |
    ForEach-Object { $_.Value.Text }) -join "`n"

Write-Note "$($viewNames.Count) views found"

$viewFailures = $script:Failures

foreach ($view in $viewNames) {
    $registered = $composition -match "AddTransient<$([regex]::Escape($view))>|AddScoped<$([regex]::Escape($view))>"

    # Reachable either from the sidebar or by navigation from another view.
    $navigable = $allText -match "NavigateToAsync<$([regex]::Escape($view))>"

    if (-not $registered) { Write-Fail "$view is not registered in the container" }
    if (-not $navigable) { Write-Fail "$view has no NavigateToAsync call anywhere" }

    if ($registered -and $navigable -and $Detailed) { Write-Pass $view }
}

if ($script:Failures -eq $viewFailures) { Write-Pass 'all views registered and navigable' }

# --- 3. Query interfaces have implementations -----------------------------------------------------

Write-Host ''
Write-Host '3. Every query interface has a registered implementation'

$queryInterfaces = [regex]::Matches($allText, 'interface\s+(I\w*Queries)\b') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

Write-Note "$($queryInterfaces.Count) query interfaces found"

$queryFailures = $script:Failures

foreach ($contract in $queryInterfaces) {
    $implemented = $allText -match ":\s*$([regex]::Escape($contract))\b"

    $registered = $composition -match "$([regex]::Escape($contract))\s*," `
                  -or $allText -match "AddScoped<[^>]*$([regex]::Escape($contract))[^>]*>"

    if (-not $implemented) { Write-Fail "$contract has no implementing class" }
    elseif (-not $registered) { Write-Fail "$contract is never registered in the container" }
    elseif ($Detailed) { Write-Pass $contract }
}

if ($script:Failures -eq $queryFailures) { Write-Pass 'every query interface is implemented and registered' }

# --- 4. Application services are registered -------------------------------------------------------

Write-Host ''
Write-Host '4. Every application service is registered in the container'

$applicationRegistrations = ($documents.GetEnumerator() |
    Where-Object { $_.Value.Name -eq 'ApplicationServiceCollectionExtensions.cs' } |
    ForEach-Object { $_.Value.Text }) -join "`n"

$services = $applicationServiceFiles |
    ForEach-Object { [regex]::Matches($_.Value.Text, 'public\s+sealed\s+class\s+(\w+Service)\b') } |
    ForEach-Object { $_ } |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

Write-Note "$($services.Count) application services found"

$serviceFailures = $script:Failures

foreach ($service in $services) {
    if ($applicationRegistrations -notmatch "$([regex]::Escape($service))>") {
        Write-Fail "$service is not registered in AddMarocLisApplication"
    }
    elseif ($Detailed) { Write-Pass $service }
}

if ($script:Failures -eq $serviceFailures) { Write-Pass 'every application service is registered' }

# --- 5. Referenced types resolve ------------------------------------------------------------------

Write-Host ''
Write-Host '5. Types named in the source resolve to a declaration'

# The check that would have caught DemoDataSeeder: a static call onto a type that does not exist.
# Restricted to the project's own naming conventions so framework and NuGet types are not flagged.
$declared = [regex]::Matches(
    $allText,
    '\b(?:class|record|struct|interface|enum)\s+(\w+)') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$declaredSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $declared)

# Static calls of the form Xxx.MethodAsync( or Xxx.Method( where Xxx looks like one of ours.
$referenced = [regex]::Matches(
    $allText,
    '(?<![\w.])((?:MarocLIS\w*|[A-Z]\w*(?:Seeder|Catalogue|Calculator|Evaluator|Parser|Renderer|Key|Rules|Sets|Identifiers|Protocol|Control)))\.\w+\s*\(') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$unresolved = @($referenced | Where-Object {
    # TextRenderer is WinForms' own text measurer; it only shares the *Renderer suffix.
    $_ -notmatch '^MarocLIS' -and $_ -ne 'TextRenderer' -and -not $declaredSet.Contains($_)
})

Write-Note "$($referenced.Count) static type references checked"

if ($unresolved.Count -eq 0) {
    Write-Pass 'every statically referenced project type is declared'
}
else {
    foreach ($type in $unresolved) { Write-Fail "type '$type' is called but never declared" }
}

# --- Result ---------------------------------------------------------------------------------------

Write-Host ''
Write-Host '--------------------------------'

if ($script:Failures -eq 0) {
    Write-Host 'All reachability checks passed.' -ForegroundColor DarkGreen
    exit 0
}

Write-Host "$script:Failures check(s) failed." -ForegroundColor Red
exit 1
