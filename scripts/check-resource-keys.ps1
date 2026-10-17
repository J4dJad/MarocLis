<#
.SYNOPSIS
    Verifies the trilingual resource catalogues against each other and against the source.

.DESCRIPTION
    Four checks, all of which run without the .NET SDK and should run in CI:

      1. All three catalogues parse as JSON.
      2. They have identical key sets - a key present in French but missing in Arabic means the
         Arabic UI silently falls back to French, which looks like a working screen.
      3. No duplicate keys within a catalogue. JSON allows them and the last one silently wins,
         so a duplicate is a translation that appears correct in the file and wrong in the app.
      4. Every resource key referenced in C# exists in the French catalogue. A key that does not
         resolve renders as the raw key on screen, or worse on a printed report.

    A missing translation is not a cosmetic defect here: domain errors carry resource keys, so an
    unresolved key can turn a regulatory refusal into an unreadable message at the moment an
    operator most needs to understand it.

.PARAMETER ResourceDirectory
    Folder holding fr.json, ar.json and en.json.

.PARAMETER SourceDirectory
    Root to scan for C# resource-key references.

.EXAMPLE
    pwsh scripts/check-resource-keys.ps1

.NOTES
    Exit code 0 when everything passes, 1 otherwise - so it can gate a build.
#>

[CmdletBinding()]
param(
    [string] $ResourceDirectory = 'src/MarocLIS.Localization/Resources',
    [string] $SourceDirectory = '.'
)

$ErrorActionPreference = 'Stop'
$failures = 0

function Write-Pass([string] $message) { Write-Host "  PASS  $message" -ForegroundColor Green }
function Write-Fail([string] $message) {
    Write-Host "  FAIL  $message" -ForegroundColor Red
    $script:failures++
}

# Flattens nested objects to dotted keys, matching JsonLocalizer.Flatten.
function Get-FlatKeys($node, [string] $prefix) {
    $keys = @()
    foreach ($property in $node.PSObject.Properties) {
        if ($property.Name -eq '$comment') { continue }

        $key = if ($prefix) { "$prefix.$($property.Name)" } else { $property.Name }

        if ($property.Value -is [System.Management.Automation.PSCustomObject]) {
            $keys += Get-FlatKeys $property.Value $key
        }
        else {
            $keys += $key
        }
    }
    return $keys
}

# Finds keys declared twice at the same path.
#
# ConvertFrom-Json silently keeps the last of a duplicated pair, so a duplicate is invisible in the
# parsed object and looks correct in the file while the application uses the other value. The raw
# text therefore has to be read line by line.
#
# The comparison is on the FULL path, not the leaf name: "Title" legitimately appears under Patient,
# Order, Sample and a dozen other sections, and flagging those would make the check useless noise.
function Get-DuplicateKeys([string] $path) {
    $lines = Get-Content $path -Encoding UTF8
    $stack = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    $duplicates = @()

    foreach ($line in $lines) {
        $trimmed = $line.Trim()

        # A section closing: "}," or "}"
        if ($trimmed -match '^\}' -and $stack.Count -gt 0) {
            $stack.RemoveAt($stack.Count - 1)
            continue
        }

        if ($trimmed -notmatch '^"([^"]+)"\s*:\s*(.*)$') { continue }

        $name = $Matches[1]
        $rest = $Matches[2]

        if ($name -eq '$comment') { continue }

        $path_ = if ($stack.Count -gt 0) { ($stack -join '.') + ".$name" } else { $name }

        if ($seen.ContainsKey($path_)) { $duplicates += $path_ } else { $seen[$path_] = $true }

        # Opening a nested object.
        if ($rest.StartsWith('{')) { $stack.Add($name) }
    }

    return $duplicates | Select-Object -Unique
}

$languages = @('fr', 'ar', 'en')
$catalogues = @{}

Write-Host ''
Write-Host 'Resource catalogue verification' -ForegroundColor Cyan
Write-Host '--------------------------------'

# --- 1. Valid JSON -------------------------------------------------------------------------------

Write-Host ''
Write-Host '1. JSON validity'

foreach ($language in $languages) {
    $path = Join-Path $ResourceDirectory "$language.json"

    if (-not (Test-Path $path)) {
        Write-Fail "$language.json is missing"
        continue
    }

    try {
        $catalogues[$language] = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Pass "$language.json parses"
    }
    catch {
        Write-Fail "$language.json is not valid JSON - $($_.Exception.Message)"
    }
}

if ($catalogues.Count -ne $languages.Count) {
    Write-Host ''
    Write-Host "ABORTED: not every catalogue could be read." -ForegroundColor Red
    exit 1
}

# --- 2. Key parity -------------------------------------------------------------------------------

Write-Host ''
Write-Host '2. Key parity (French is the reference and the fallback)'

$keys = @{}
foreach ($language in $languages) {
    $keys[$language] = @(Get-FlatKeys $catalogues[$language] $null)
    Write-Host "        $language : $($keys[$language].Count) keys"
}

foreach ($language in @('ar', 'en')) {
    $missing = @($keys['fr'] | Where-Object { $keys[$language] -notcontains $_ })
    $extra = @($keys[$language] | Where-Object { $keys['fr'] -notcontains $_ })

    if ($missing.Count -eq 0) { Write-Pass "$language has no missing keys" }
    else { Write-Fail "$language is missing $($missing.Count): $($missing -join ', ')" }

    if ($extra.Count -eq 0) { Write-Pass "$language has no orphaned keys" }
    else { Write-Fail "$language has $($extra.Count) keys absent from French: $($extra -join ', ')" }
}

# --- 3. Duplicates -------------------------------------------------------------------------------

Write-Host ''
Write-Host '3. Duplicate keys (JSON allows them; the last one silently wins)'

foreach ($language in $languages) {
    $duplicates = @(Get-DuplicateKeys (Join-Path $ResourceDirectory "$language.json"))

    if ($duplicates.Count -eq 0) { Write-Pass "$language has no duplicates" }
    else { Write-Fail "$language has duplicates: $($duplicates -join ', ')" }
}

# --- 4. Keys referenced in code -------------------------------------------------------------------

Write-Host ''
Write-Host '4. Keys referenced in C# resolve against the catalogue'

$known = [System.Collections.Generic.HashSet[string]]::new([string[]] $keys['fr'])

# Literal resource keys. Interpolated ones - T($"Gender.{value}") - cannot be checked statically
# and are covered by the enum-coverage check below.
#
# The T(...) pattern accepts a trailing comma as well as a closing paren: a key with format
# arguments - T("Admin.LockedOutNotice", until) - is exactly as capable of not resolving as one
# without, and matching only T("key") silently exempted every formatted message in the application.
$patterns = @(
    '\bT\("([^"]+)"\s*[,)]',
    'Localizer\["([^"]+)"\]',
    'Localizer\.Get\("([^"]+)"',
    'Localizer\.Format\("([^"]+)"',
    '_localizer\.Get\("([^"]+)"',
    '_localizer\.Format\("([^"]+)"',
    'localizer\["([^"]+)"\]',
    'Label\("([^"]+)",',
    'Field\("([^"]+)",',
    'Error\.(?:Validation|NotFound|Conflict|Forbidden|Regulatory)\("([^"]+)"',
    'InstrumentParseIssue\.(?:Blocking|Warning)\("([^"]+)"',

    # The MasterDataEditor field helpers take the key as their first argument, so every label,
    # choice, lookup, number and tick box in the reference-data and catalogue editors was invisible
    # to this check - which is roughly two hundred keys in the editors the laboratory uses most.
    #
    # TextField is named explicitly even though the Field(...) pattern above happens to catch it
    # too - that pattern carries no word boundary, so "TextField(" matches it by accident. Relying
    # on an accident to cover 131 call sites is not a check, so the intent is written down here.
    '\b(?:TextField|Text|Choice|Lookup|Number|Check|OptionalDate)\("([^"]+)"\s*,',

    # Notice takes its key fifth, after the bounds.
    '\bNotice\(\s*-?\d+\s*,\s*-?\d+\s*,\s*-?\d+\s*,\s*-?\d+\s*,\s*"([^"]+)"',

    # The base's chrome: MasterDataEditor(localizer, dialogs, "titleKey", subtitle, w, h).
    '\bbase\(\s*localizer\s*,\s*dialogs\s*,\s*"([^"]+)"',

    # ... and the same, where the title is chosen between "new" and "edit" by a ternary. Both arms
    # are captured, because an editor that is reached far more often through Edit than through New
    # would otherwise have its one visible title unchecked.
    '\bbase\(\s*localizer\s*,\s*dialogs\s*,[^;]{0,120}?\?\s*"([^"]+)"\s*:\s*"([^"]+)"',

    # Resource keys carried as data. A row record that computes why an invoice cannot be claimed,
    # or why a payer will reject one, returns the reasons as keys for the view to resolve - so the
    # key never appears next to a T(...) and was invisible here. ClaimCandidateRow.Blockers had
    # been doing this since it was written.
    '\b(?:blockers|problems|issues|reasons)\.Add\("([^"]+)"\)',

    # CatalogView's Header(grid, "column", "key") applies a header text by key.
    '\bHeader\([^,]+,\s*"[^"]+"\s*,\s*"([^"]+)"\s*\)',

    # MasterDataView has its own Header overload taking ("column", "key") tuples, so none of its
    # grid headers matched the pattern above - six grids' worth of column titles, unverified.
    # The second literal must look like a dotted key, which is what keeps this off the ("code", 10,
    # ...) column definitions sitting beside them.
    '\(\s*"[^"]+"\s*,\s*"([A-Za-z]\w*(?:\.\w+)+)"\s*\)',

    # The instrument sink stores issues as a resource key with a detail, so the message log reads
    # in the operator's language. Those keys reach a screen exactly like a T(...) one does.
    '\bDescribe\("([^"]+)"',

    # A key chosen by a ternary inside the lookup itself - T(enabling ? "X.EnablePrompt" :
    # "X.DisablePrompt", code) - was invisible to the T("..." pattern, and both of those keys had
    # never existed. Both arms are captured.
    '(?:\bT\(|[Ll]ocalizer\[|[Ll]ocalizer\.(?:Get|Format)\()\s*[^;()"\[\]]*?\?\s*"([^"]+)"\s*:\s*"([^"]+)"',

    # The mock-up's own grid and label helpers: a column header given by key, a cell written as
    # "@Some.Key", and the sidebar's { key: ..., label: "Nav.X" } entries.
    '\bh:\s*"([^"]+)"',
    '"@([A-Za-z][\w.]*)"',
    '\blabel:\s*"([^"]+)"',
    '\bgroup:\s*"([^"]+)"'
)

$referenced = @{}

# The C# and Razor sources, plus the UI mock-up. The mock-up calls T("key") against the same three catalogues
# and had never been checked, so it was free to invent keys that render as "[[Some.Key]]" in a
# document whose whole purpose is to show the laboratory what the screens will look like. Checking
# it also makes it fail when a key is renamed, which is the only thing that keeps a 300 KB mock-up
# from going quietly stale.
$scanned = @(Get-ChildItem -Path $SourceDirectory -Recurse -Filter *.cs -File) +
           # The phone's screens (MarocLIS.Mobile) name their texts in Razor markup, as @T("Key").
           @(Get-ChildItem -Path $SourceDirectory -Recurse -Filter *.razor -File) +
           @(Get-ChildItem -Path (Join-Path $SourceDirectory 'docs') -Filter *.html -File `
                           -ErrorAction SilentlyContinue)

foreach ($file in $scanned) {
    if ($file.FullName -match '[\\/](obj|bin)[\\/]') { continue }

    $text = Get-Content $file.FullName -Raw -Encoding UTF8

    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($text, $pattern)) {

            # Every capture group, not only the first. A pattern can legitimately carry two keys -
            # the two arms of a ternary handed to a dialog title, say - and reading group 1 alone
            # would check one of them and silently exempt the other, which is how
            # 'Coverage.EditPayer' got into a screen without ever being verified.
            for ($group = 1; $group -lt $match.Groups.Count; $group++) {
                $key = $match.Groups[$group].Value
                if ([string]::IsNullOrWhiteSpace($key)) { continue }

                # Every real resource key is either nested ("Patient.Cin") or a flat domain error
                # key ("Patient_CinInvalid"), so it always contains a dot or an underscore.
                # Requiring that filters out the incidental string literals the patterns would
                # otherwise catch - an HL7 segment name passed to Field("MSH", 7), a font family
                # passed to new Font(...).
                if ($key -notmatch '[._]') { continue }

                if (-not $referenced.ContainsKey($key)) { $referenced[$key] = @() }
                $referenced[$key] += $file.Name
            }
        }
    }
}

$unresolved = @($referenced.Keys | Where-Object { -not $known.Contains($_) } | Sort-Object)

Write-Host "        $($referenced.Count) literal keys referenced in code"

if ($unresolved.Count -eq 0) {
    Write-Pass 'every referenced key exists in the catalogue'
}
else {
    foreach ($key in $unresolved) {
        $sources = ($referenced[$key] | Select-Object -Unique) -join ', '
        Write-Fail "'$key' is referenced in $sources but absent from the catalogue"
    }
}

# --- 5. Enum coverage for interpolated keys --------------------------------------------------------

Write-Host ''
Write-Host '5. Enum-prefixed key groups are complete'

# Prefix in the catalogue -> enum name in the domain. Populated for every group a view interpolates.
$enumGroups = [ordered] @{
    'Gender'          = 'Gender'
    'Priority'        = 'OrderPriority'
    'OrderStatus'     = 'LabOrderStatus'
    'OrderItemStatus' = 'LabOrderItemStatus'
    'OrderOrigin'     = 'OrderOrigin'
    'Interpretation'  = 'ResultInterpretation'
    'Qualification'   = 'ProfessionalQualification'
    'PayerKind'       = 'PayerKind'
    'SampleStatus'    = 'SampleStatus'
    'RejectionReason' = 'SampleRejectionReason'
    'QcOutcome'       = 'QcOutcome'
    'QcLevel'         = 'QcLevel'
    'EqaOutcome'      = 'EqaOutcome'
    'NcSeverity'      = 'NonConformitySeverity'
    'NcStatus'        = 'NonConformityStatus'
    'EquipmentStatus' = 'EquipmentStatus'
    'ResultValueType' = 'ResultValueType'
    'PatientAlertSeverity' = 'PatientAlertSeverity'
    'ConsentRequirement' = 'ConsentRequirement'
    'ReflexCondition' = 'ReflexCondition'
    'ReportCompleteness' = 'ReportCompleteness'
    'ReportAmendmentKind' = 'ReportAmendmentKind'
    'DeliveryChannel'  = 'DeliveryChannel'
    'ReportRecipientKind' = 'ReportRecipientKind'
    'DeliveryStatus'   = 'DeliveryStatus'
    'InstrumentProtocol' = 'InstrumentProtocol'
    'InstrumentTransportKind' = 'InstrumentTransportKind'
    'InstrumentDirection' = 'InstrumentDirection'
    'MessageDirection' = 'MessageDirection'
    'InstrumentMessageProcessingStatus' = 'InstrumentMessageProcessingStatus'
    'ConsentPurpose'  = 'ConsentPurpose'
    'ConsentChannel'  = 'ConsentChannel'
    'BloodGroup'      = 'BloodGroup'
    'MoroccanRegion'  = 'MoroccanRegion'
    'BeneficiaryRelationship' = 'BeneficiaryRelationship'
    'PaymentMethod'   = 'PaymentMethod'
    'StockItemKind'      = 'StockItemKind'
    'StorageCondition'   = 'StorageCondition'
    'StockMovementKind'  = 'StockMovementKind'
    'EquipmentKind'       = 'EquipmentKind'
    'MaintenanceKind'     = 'MaintenanceKind'
    'CalibrationOutcome'  = 'CalibrationOutcome'
    'SecurityEventKind'   = 'SecurityEventKind'
    'AppLanguage'         = 'AppLanguage'
    'LabSiteKind'         = 'LabSiteKind'
    'SequencePurpose'     = 'SequencePurpose'
    'PaymentStatus'       = 'PaymentStatus'
    'ClaimStatus'         = 'ClaimStatus'
    'ActionStatus'        = 'CorrectiveActionStatus'
    'ActionKind'          = 'ActionKind'
    'PrescriberKind'      = 'PrescriberKind'
    'ReportDeliveryPreference' = 'ReportDeliveryPreference'

    # Groups added after the third review found the audit trail showing "AuditOperation.Update":
    # every prefix a view interpolates whose enum lives in the domain.
    'AuditOperation'      = 'AuditOperation'
    'DataAccessKind'      = 'DataAccessKind'
    'SampleDefect'        = 'SampleDefect'
    'DocumentKind'        = 'DocumentKind'
    'DocumentVersionStatus' = 'DocumentVersionStatus'
    'DetectionSource'     = 'DetectionSource'
    'InvoiceStatus'       = 'InvoiceStatus'
    'PurchaseOrderStatus' = 'PurchaseOrderStatus'
    'QuoteRequestStatus'  = 'QuoteRequestStatus'
    'SupplierInvoiceStatus' = 'SupplierInvoiceStatus'
    'SupplierPaymentMethod' = 'SupplierPaymentMethod'
    'InstrumentContractKind' = 'InstrumentContractKind'
    'CommitmentBasis'     = 'CommitmentBasis'
    'StorageUnitKind'     = 'StorageUnitKind'
    'InventoryCountStatus' = 'InventoryCountStatus'
    'ExpiryAlertLevel'    = 'ExpiryAlertLevel'
    'AccidentEventKind'   = 'AccidentEventKind'
    'AccidentKind'        = 'AccidentKind'
    'ExposureType'        = 'ExposureType'
    'ContractType'        = 'ContractType'
    'CredentialKind'      = 'CredentialKind'
    'EmployeeJob'         = 'EmployeeJob'
    'ExitReason'          = 'ExitReason'
    'HabilitationActivity' = 'HabilitationActivity'
    'HabilitationLevel'   = 'HabilitationLevel'
    'LeaveStatus'         = 'LeaveStatus'
    'LeaveType'           = 'LeaveType'
    'MedicalFitness'      = 'MedicalFitness'
    'PayrollExemption'    = 'PayrollExemption'
    'PayrollStatus'       = 'PayrollStatus'
    'ShiftKind'           = 'ShiftKind'
    'StaffCategory'       = 'StaffCategory'
}

foreach ($prefix in $enumGroups.Keys) {
    $enumName = $enumGroups[$prefix]

    $declaration = Select-String -Path (Join-Path $SourceDirectory 'src/MarocLIS.Domain/*/*.cs') `
                                 -Pattern "enum $enumName\b" -List -ErrorAction SilentlyContinue

    if (-not $declaration) {
        Write-Fail "$prefix : enum $enumName was not found in the domain"
        continue
    }

    $lines = Get-Content $declaration.Path
    $members = @()

    # A [Flags] enum's None is the absence of a value, never shown as a label (SampleDefect).
    $isFlags = $declaration.LineNumber -ge 2 -and $lines[$declaration.LineNumber - 2].Trim() -eq '[Flags]'

    # Members are collected per comma-separated declarator, not per line. Several enums declare
    # more than one on a line - "OPositive = 1, ONegative = 2," - and a line-at-a-time reader
    # silently checked only the first of each, reporting BloodGroup as 5 members when it has 9.
    # An enum-coverage check that under-counts is worse than no check: it reports PASS for keys
    # nobody verified.
    for ($i = $declaration.LineNumber; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line -eq '}') { break }
        if ($line.StartsWith('//') -or $line.StartsWith('/*') -or $line.StartsWith('*')) { continue }
        if ($line.StartsWith('[')) { continue }   # an attribute such as [Flags]

        foreach ($declarator in $line.Split(',')) {
            $name = $declarator.Trim()
            if ($name -match '^([A-Z]\w*)\s*(=.*)?$') {
                if ($isFlags -and $Matches[1] -eq 'None') { continue }
                $members += $Matches[1]
            }
        }
    }

    $missing = @($members | Where-Object { -not $known.Contains("$prefix.$_") })

    if ($missing.Count -eq 0) { Write-Pass "$prefix ($($members.Count) members)" }
    else { Write-Fail "$prefix is missing: $($missing -join ', ')" }
}

# --- 6. The mock-up's embedded catalogues match the source of truth -------------------------------

Write-Host ''
Write-Host "6. docs/ui-mockup.html embeds the current catalogues"

# The mock-up is a single self-contained file, so it carries its own copy of all three catalogues -
# and a copy is a thing that goes stale. It did: the file rendered "Claim.Outstanding" and
# "Nav.SerumBank" as raw keys on screen while every check passed, because the checks read the
# catalogues on disk and the page read the ones inside itself. Comparing the two is the only thing
# that keeps a 550 KB document honest.
$mockup = Join-Path $SourceDirectory 'docs/ui-mockup.html'

if (-not (Test-Path $mockup)) {
    Write-Host '        docs/ui-mockup.html is absent - skipped' -ForegroundColor DarkGray
}
else {
    $mockupLines = Get-Content $mockup -Encoding UTF8

    foreach ($language in $languages) {
        $open = -1

        for ($i = 0; $i -lt $mockupLines.Count; $i++) {
            if ($mockupLines[$i] -match "<script id=`"cat-$language`"") { $open = $i + 1; break }
        }

        if ($open -lt 0) {
            Write-Fail "the mock-up has no embedded $language catalogue"
            continue
        }

        # Trimmed before comparing: matching '</script>' exactly meant that indenting the closing
        # tag by two spaces sent this to the end of the file, and the check then reported that the
        # catalogue "does not parse" - true, but about 400 KB of HTML rather than about the
        # catalogue. A check should not be breakable by whitespace it does not care about.
        $close = $open
        while ($close -lt $mockupLines.Count -and $mockupLines[$close].Trim() -ne '</script>') {
            $close++
        }

        try {
            $parsed = ($mockupLines[$open..($close - 1)] -join "`n") | ConvertFrom-Json
            $embedded = @(Get-FlatKeys $parsed $null)
        }
        catch {
            Write-Fail "the mock-up's embedded $language catalogue does not parse"
            continue
        }

        $source = $keys[$language]

        $missing = @($source | Where-Object { $embedded -notcontains $_ })
        $extra = @($embedded | Where-Object { $source -notcontains $_ })

        if ($missing.Count -eq 0 -and $extra.Count -eq 0) {
            Write-Pass "$language matches ($($embedded.Count) keys)"
        }
        elseif ($missing.Count -gt 0) {
            Write-Fail ("the mock-up's $language catalogue is behind by $($missing.Count) " +
                        "key(s), e.g. $(($missing | Select-Object -First 5) -join ', ') " +
                        '- re-embed it from src/MarocLIS.Localization/Resources')
        }
        else {
            Write-Fail ("the mock-up's $language catalogue has $($extra.Count) key(s) the source " +
                        "does not, e.g. $(($extra | Select-Object -First 5) -join ', ')")
        }
    }
}

# --- Summary ---------------------------------------------------------------------------------------

Write-Host ''
Write-Host '--------------------------------'

if ($failures -eq 0) {
    Write-Host 'All resource checks passed.' -ForegroundColor Green
    exit 0
}

Write-Host "$failures check(s) failed." -ForegroundColor Red
exit 1
