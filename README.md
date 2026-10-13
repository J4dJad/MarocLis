# MarocLIS — Système de gestion de laboratoire d'analyses de biologie médicale

Trilingual (French / Arabic / English) laboratory information system for Moroccan medical biology
laboratories. C# / .NET 10, WinForms desktop client, SQL Server Express, LAN client–server.

---

## Build status

**The solution builds clean and the test suite passes: 398 tests, 0 failures, 0 errors, 0 compiler
warnings.** Verified with SDK 10.0.112 on 13 October 2026, and run against a real SQL Server 2022
(see [Database](#database)).

```bash
dotnet build MarocLIS.sln
```

```bash
dotnet test MarocLIS.sln
```

| | |
|---|---|
| `dotnet restore` | ✅ 8 projects, no vulnerability warnings |
| `dotnet build` | ✅ 0 errors, 0 warnings |
| `dotnet test` | ✅ 398 passed, 0 failed |
| EF migration generated, schema script produced | ✅ 65 application tables (66 with `__MigrationsHistory`), 116 indexes, 15 schemas, 4 triggers |
| Application starts, DI graph resolves, EF model builds | ✅ |
| Run against a live SQL Server | ✅ SQL Server 2022: migration, seeding, sign-in and the main write paths |

The first compile was a real step with real work in it, as the previous version of this section
predicted. It took about twenty-five distinct fixes over six rounds, with the raw error count
peaking at 355: clearing the declaration-level errors each time revealed the next layer, because
the compiler does not run body-level analysis on a file that has not finished binding. Two single
edits cleared 298 and 41 of those — a non-null `Column(name)` accessor for the grids, whose string
indexer returns null, and renaming a `Text` helper that shadowed `Control.Text` on every
reference-data editor.

The test suite then failed 11 of 316, and only two of those were wrong expectations. The rest were
real defects the tests had been written to catch and never got the chance to:

| Defect | Consequence |
|---|---|
| CKD-EPI eGFR divided creatinine by both 10 and 88.4 | Every eGFR ~6.3× too high; renal failure reported as normal filtration, and the KDIGO threshold could never fire |
| Reference-range specificity ranked sex above age band | A newborn's haemoglobin resolved against the adult male interval: physiologically normal 20 g/dL flagged High, 25 flagged critical |
| ASTM timestamps parsed as UTC then converted to local | Every collection and measurement time shifted by the local offset — one hour in winter, two in summer |
| `Unvalidate` checked the status before the reported flag | A reported analysis was refused with "not validated" instead of "issue an amendment" |
| `PrescriberRow` did not project `SendPartialReports` | Editing any prescriber silently reset their partial-report setting |
| File-drop transport never raised `Disconnected` | An unmounted share looked identical to a quiet day on the bench, indefinitely |

The build is now warning-clean. Of the 135 analyzer warnings it started with, the substantive ones
were fixed — a culture-dependent `ToUpper()` on a site code, which under a Turkish-culture
workstation would have mapped `i` to `İ` and let a laboratory allocate a duplicate code against a
unique index; a double enumeration of the pending-migrations sequence; a `ContainsKey` followed by
an indexer; a `JsonSerializerOptions` rebuilt on every write. Five rules are suppressed
solution-wide in `Directory.Build.props`, each with the reason it does not apply — `CA1848` and
`CA1873` because this application has no hot logging path, `CA1716` and `CA1711` because the names
they object to (`Error`, `RolePermission`, `ResultFlag`) are the right domain names in a C#-only
solution, `CA1000` because it objects to the `Result<T>` factory pattern. `CA1305` is suppressed in
the desktop project alone: there the ambient culture *is* the operator's chosen language, because
`CultureManager.SetLanguage` sets it. At every boundary where culture must not vary — the numbering
series, the ASTM checksum, a TCP port, the report renderers — the provider is passed explicitly and
the rule stays on.

## Payer maintenance and the audit-trail viewer

Two screens that the domain, the schema and the seeded data all assumed existed:

**Payer maintenance** (`Nav.MasterData` → Payers). The AMO reimbursement rates, the national
reference tariff flag, the third-party-payment agreements and the INPE-on-claim rules were seeded
from chosen values — `ReferenceDataSeeder` calls them *"starting points an administrator confirms
against the current convention, not law"* — and there was nowhere to confirm them. Every
installation would have billed AMO at unverified rates with a hand-written `UPDATE` as the only
remedy. `Payer` also had no `Validate()` at all; it now refuses what is arithmetically or legally
impossible (a rate outside 0–1, an AMO scheme priced off the laboratory's own list, a self-pay
payer that reimburses something) and *reports* what is merely administratively incomplete, because
a rule that the shipped data itself violates is the wrong rule. A test asserts the seeded rows
satisfy it.

The rate is entered as a percentage and stored as a fraction, converted in one place. A convention
reads "70 %", `ActPricingCalculator` multiplies by 0.70, and the gap between those two is a factor
of a hundred applied to real money.

**Audit-trail viewer** (`Nav.AuditTrail`). `AuditEntry`, `DataAccessLog` and `SecurityEvent` were
written meticulously and could not be read by the application at all — the answer to "who opened
this patient's file?" was a DBA with SSMS, which is not an answer a laboratory can give under
loi n° 08-09 and not the retrievable change history ISO 15189 §5.8 asks for. Three tabs over one
date range, with break-glass accesses and failed logins sorted to the top because they are a
handful of rows among thousands. Entering a medical record number turns the consultations tab into
a subject-access answer: scoped to that patient and complete rather than capped, since a truncated
answer to a statutory request is not one. Reading it is itself logged as a consultation — an audit
screen exempt from auditing would be the one blind spot in the system.

### Checks that were not checking

Both screens were built against the project's own verification scripts, and adding to them exposed
four holes in those scripts. Each is worth recording, because the lesson has now repeated often
enough to be the house rule: **a check that passes is only as good as what it looks at.**

| Hole | What it was hiding |
|---|---|
| `check-reachability.ps1` matched `Task<[^>]*>`, and a negated class cannot cross the `>` it excludes | Every method returning `Task<Result<IReadOnlyList<T>>>` — nearly every read method in the application — was never checked. Fixing it found `PatientRegistrationService.FindDuplicatesAsync` dead since it was written |
| `check-resource-keys.ps1` read only capture group 1 | A dialog title chosen by a ternary had one arm checked and the other exempt |
| It had no pattern for keys carried as data | `ClaimCandidateRow.Blockers` returns resource keys; one of them, `Invoice_ClaimRequiresPrescriberInpe`, had never existed in any catalogue and rendered as a raw key |
| It matched `Header(grid, "col", "key")` but not the tuple-form overload | Six grids' worth of column headers in `MasterDataView`, unverified |

`FindDuplicatesAsync` is the sharpest of those. Its own comment says it exists *"so the screen can
warn while the clerk is still looking at the patient, rather than after they have pressed save and
turned away"* — and nothing called it, so the only duplicate warning came after Save, by which time
the person has left the counter. Two files for one patient split a clinical history in half and
merging them afterwards is the one correction this application cannot make cleanly. It now runs as
the name, CIN and date-of-birth fields lose focus.

The same widening also showed that `Invoice_ClaimRequiresInpe` was being used two incompatible ways
— as a formatted error carrying the invoice number, and as a bare blockers-column key resolved with
no arguments, which printed a literal `{0}` on the claims screen. The grid now has its own short,
parameter-free `Claim.*` keys.

## Database

The schema now exists as an EF migration — `src/MarocLIS.Infrastructure/Persistence/Migrations` —
and as a reviewable idempotent script, `db/MarocLIS-schema.sql`: 65 application tables across 15
schemas — 66 `sys.tables` rows once EF's own `cfg.__MigrationsHistory` is counted — 116 indexes of
which 42 are unique, and the four append-only triggers.

```bash
dotnet ef database update --project src/MarocLIS.Infrastructure --startup-project src/MarocLIS.Infrastructure
```

Generating the migration surfaced a defect that had made the context unusable on any machine: the
site-scope filter built its `Count` expression against `IReadOnlyList<Guid>`, and because reflection
does not surface an interface's inherited members, the lookup threw while the model was being
built. `nameof` hid it — it compiles to `"Count"` whichever interface you name.

Three further things changed, all of them about the two setup paths disagreeing:

- **The append-only triggers moved into the migration.** They were applied only from
  `DatabaseInitializer`'s `EnsureCreated` branch, so a site deploying the way `docs/DEPLOYMENT.md`
  tells it to — by `dotnet ef database update` — would have got all 65 tables and none of the audit
  protection. The control the architecture notes describe would have been absent from exactly the
  installations that get audited.
- **`DatabaseInitializer` now migrates instead of calling `EnsureCreated`.** `EnsureCreated` builds
  the tables but writes nothing to `cfg.__MigrationsHistory`, so a database stood up that way looks
  entirely unmigrated: the next `database update` tries to create all 65 tables again and the site
  has no upgrade path without hand-writing history rows.
- **The four triggered tables now declare `HasTrigger`.** From EF Core 7 the SQL Server provider
  saves through an `OUTPUT` clause and SQL Server refuses `OUTPUT` on a table with an enabled
  trigger. Without the declaration every audit write would fail — and take down the write it was
  recording with it.

### Verified against a live engine (13 October 2026)

The migration was applied to an empty SQL Server 2022 database and the application's own services
were driven against it: 66 tables, 116 indexes, the four triggers enabled and refusing `UPDATE` and
`DELETE` on the audit tables, reference and demo seeding run twice without duplicates, `admin`
sign-in, the dashboard, patient search, order registration, lot reception, and the four new screens'
complete flows (document versions through to entry into force, an EQA round through to the
non-conformity it opens, a purchase order through approval, sending and two partial receipts, and
a year of statistics). `HasPendingModelChanges()` is false.

Three defects only a real server could show were found and fixed:

- **The migration could not be applied.** `ResultRevisions` had a cascading delete from
  `TestResults`, and SQL Server refuses an `INSTEAD OF` trigger on a table that is the target of a
  cascade, so `TR_ResultRevisions_NoUpdate` failed and the whole migration with it. The foreign key
  is now `Restrict` (no code deletes a test result) in the configuration, the migration, its
  designer, the snapshot and `db/MarocLIS-schema.sql`.
- **Adding a child to a loaded aggregate failed as a concurrency conflict.** Keys are assigned in
  the domain (`Guid.CreateVersion7()`), but EF took them for store-generated keys, so a new lot on a
  stock item, a new version on a document or a new line on a purchase order was sent as an `UPDATE`
  that matched nothing. Every `Guid` primary key is now `ValueGeneratedNever` (`LisDbContext`); the
  schema is unchanged, only the snapshot lost its `ValueGeneratedOnAdd` lines.
- **Every use case that opens a transaction threw.** `EnableRetryOnFailure` installs a strategy that
  refuses user-initiated transactions, and all the gapless-number paths (order registration,
  invoicing, credit notes, non-conformities, purchase orders) open one.
  `TransactionAwareExecutionStrategy` keeps the retries outside a transaction and runs a save inside
  one once, leaving a transient failure to roll the whole unit back.

Not exercised yet: two workstations allocating numbers at the same instant (the `UPDLOCK, HOLDLOCK`
path), and the report renderer, which needs the desktop's QuestPDF registration.

### Fixed: global query filters on required principals

The first model build logged five warnings of one kind, and they were a real defect. A globally
filtered principal that is the required end of a navigation makes EF use an inner join, so
filtering the principal out takes its dependents with it — rows still in the database, silently
absent from every query that resolves the navigation. No error, no gap in a sequence, just less
data than there should be.

| Principal (was filtered) | Required dependent (unfiltered) | What retiring the principal would have done |
|---|---|---|
| `AnalysisTest` | `LabOrderItem`, `TestParameter`, `TestPanelItem` | Dropped every historical order item using that analysis from worklists, reports and invoices |
| `Department` | `AnalysisTest` | Dropped the analyses of a retired discipline, and with them their report section |
| `AppUser` | `UserRole` | Made an authorization decision out of a delete flag |
| `Payer` | `PatientCoverage` | Made covered patients look uninsured and repriced their invoices at the full rate |

Two facts settled the fix. **Nothing in the application calls `IRepository.SoftDelete`** — the
method exists, no code path uses it, and `Patient.Anonymize` pseudonymises in place without
touching the flag, so `IsDeleted` is never true anywhere. And **all four types already model
retirement with `IsActive`**, which the catalogue, user and billing screens already filter on
deliberately via their `includeInactive` toggles.

So the soft-delete columns on these four bought no protection and cost the defect. They are gone:
retiring referenced master data is `IsActive`, and the records that referenced it keep resolving it
for as long as the ten-year retention requires. Because nothing ever set the flag, this changes no
runtime behaviour — it removes four unused columns per table and the filter that was breaking the
joins.

`ISoftDeletable` stays where deletion genuinely should be soft and filtered: the clinical and
accounting records (`Patient`, `LabOrder`, `Sample`, `Invoice`'s neighbours, `LabSite`,
`Prescriber`, `Instrument`, `StockItem`, `Supplier`, `TestPanel`, `QcMaterial`).

**`Department` is the one that makes the case for the guard.** It was not among the original five —
while both it and `AnalysisTest` were filtered, the pair matched and EF said nothing. Removing
`AnalysisTest`'s filter turned it into an unmatched filtered principal, and it surfaced
immediately only because the warning had been escalated to an exception for that build. Fixing one
instance of this shape can create another, silently.

The durable guard is `QueryFilterShapeTests`, which recomputes the shape from the EF model — no
database needed, since model building is pure metadata — and fails on any filtered principal with
an unfiltered required dependent outside a reviewed allow-list. The allow-list holds one entry,
`LabOrder -> LabOrderItem`, where the dependent is the principal's own aggregate child and
disappearing with it is correct; the residual case it does *not* cover is written down next to it.
A second test names the four master-data types individually, because the structural test would pass
just as well if somebody added them to the allow-list, and for those four that would be the wrong
answer. `TheModelBuilds` is in the same file and would have caught the site-scope filter's `Count`
lookup — an exception during model construction that took the whole context down on every machine.

The static checks below still run, still pass, and are still worth running first because they catch
a class of defect no compiler and no unit test will:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-resource-keys.ps1
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-reachability.ps1
```

| Check | Result |
|---|---|
| `fr.json` / `ar.json` / `en.json` parse as JSON | ✅ |
| Identical key sets across all three | ✅ 2 210 keys each, 0 missing, 0 orphaned |
| No duplicate keys within a catalogue | ✅ (JSON allows them and the last silently wins) |
| Every resource key referenced in C# **and in the mock-up** exists in the catalogue | ✅ 1 611 literal keys |
| Enum-prefixed key groups complete | ✅ 51 groups, every member present |
| `docs/ui-mockup.html` embeds the current catalogues | ✅ 2 210 keys × 3, matching exactly |
| Every public service method has a caller outside its own file | ✅ |
| Every view registered in DI and reachable from the shell | ✅ 23 views |
| Every query interface implemented and registered | ✅ 16 interfaces |
| Every application service registered in the container | ✅ 18 services |
| Every statically referenced project type is declared | ✅ |

`check-reachability.ps1` exists because most of the defects in this repository's history were of one
kind: finished code with no path to it from the user interface. A service method with no caller
looks complete in a review and does nothing in a laboratory. Its fifth check — that a statically
called type actually exists — is the one that would have caught the `DemoDataSeeder` call that broke
the build.

Check 6 of the resource script was added for the same reason, one level up. `docs/ui-mockup.html` is
self-contained, so it carries its own copy of all three catalogues — and a copy goes stale. It had:
the page rendered `Claim.Outstanding` and `Nav.SerumBank` as raw keys on screen while every check
reported PASS, because the checks read the catalogues on disk and the page read the ones inside
itself. **Verifying the source of truth says nothing about a copy of it**, which is the general
lesson and the reason the comparison is now automatic.

The checker has been widened six times now, and every widening found real defects rather than
confirming the previous pass. It originally matched only `T("key")`, which silently exempted every
formatted message; then only one enum member per declaration line, which under-counted nine-member
enums as five; then it did not match the `MasterDataEditor` field helpers, which take the resource
key as an argument, so roughly two hundred keys in the editors a laboratory uses most were never
checked at all — widening that took the verified count from 1 120 to 1 404 and immediately surfaced
four invented keys and five whole enum sections missing from all three catalogues. The three most
recent are described under *Checks that were not checking* above.

**A check that passes is only as good as what it looks at**, which is why the count is printed: a
number that stops growing while the code does is itself the finding. It currently reads 1 611.

Building needs the .NET 10 SDK, not just the runtime:

```bash
dotnet --list-sdks
```

If that prints nothing, install it via your IT software catalogue or from
https://dotnet.microsoft.com/download/dotnet/10.0 (the user-scoped installer needs no admin rights).

---

## Why the architecture looks the way it does

The layering is conventional; the parts that are *not* conventional are the ones driven by
regulation, and they are worth knowing before changing anything:

- **Business rules live in the domain, not the UI.** Only a qualified biologist may release a result
  (loi n° 12-01). That is enforced by `AppUser.CanBiologicallyValidate`, which checks the
  qualification, the professional-order registration and its expiry — *in addition to* the
  permission grant. Removing the button would not be a control; refusing the operation is.
- **Nothing clinical or accounting is ever deleted.** `ISoftDeletable` everywhere, global query
  filters in `LisDbContext`. Medical records carry a ten-year retention horizon; the DGI requires an
  unbroken invoice series. The right-to-erasure path is `Patient.Anonymize`, which pseudonymises in
  place rather than deleting.
- **Auditing is structural.** `AuditSaveChangesInterceptor` writes a before/after entry for every
  change in the same transaction as the change. `aud.*` tables carry `INSTEAD OF UPDATE, DELETE`
  triggers, so the application cannot rewrite its own audit trail.
- **Document numbers are allocated under a pessimistic row lock**, not by `IDENTITY` or a database
  sequence. Both of those burn values on rollback and leave gaps in an invoice series that Moroccan
  tax law requires to be unbroken and chronological.
- **Barcodes are unique at the database level.** `UX_Samples_Barcode`. A reused barcode attaches one
  patient's tube to another patient's order; that check does not belong in application code alone.
- **Translations are data, not compiled resources.** JSON files next to the executable, so a
  laboratory can correct an Arabic term without Visual Studio, and a fourth language is a new file.

## Solution layout

```
MarocLIS.sln
├── src/
│   ├── MarocLIS.Domain            entities, value objects, business invariants — zero dependencies
│   ├── MarocLIS.Application       use cases, pricing engine, formula engine, abstractions
│   ├── MarocLIS.Infrastructure    EF Core + SQL Server, audit interceptor, security, seeding
│   ├── MarocLIS.Localization      trilingual catalogues, culture and RTL management
│   ├── MarocLIS.Instruments       ASTM E1381/E1394 and HL7 v2.5 protocol layer
│   ├── MarocLIS.Reporting         PDF reports and invoices (QuestPDF), barcode labels
│   └── MarocLIS.Desktop           WinForms client
└── tests/
    └── MarocLIS.Tests
```

## Module status

| Module | State |
|---|---|
| Domain model — patients, orders, samples, results, reports | complete |
| Domain model — billing, quality, inventory, equipment, audit | complete |
| Moroccan identifiers (CIN, ICE, IF, CNSS, INPE) with validation | complete |
| Trilingual localisation, RTL, culture handling | complete, key parity verified |
| Westgard multirule QC evaluator (1-2s … 12-x, R-4s, 7-T) | complete |
| AMO / TNR pricing engine with payer–patient split | complete |
| Calculation engine for derived parameters | complete |
| EF Core mapping, indexes, global filters, audit interceptor | complete |
| Reference-data + starter-catalogue seeding | complete |
| Authentication, permissions, password policy | complete |
| ASTM E1394 and HL7 v2.5 parsers, MLLP, ACK generation | complete |
| Order registration, result entry, technical validation | complete |
| Biological validation, critical-result communication, report release | complete |
| Instrument transports (serial / TCP / file drop), drivers, analyser simulator | complete |
| Trilingual PDF report renderer + barcode label renderer | complete |
| WinForms shell, login, dashboard, patient search, order registration, result entry, validation | complete |
| Sample collection and reception screens | complete |
| Billing screens — unbilled work, receivables, payments, cash session | complete |
| Quality screens — QC entry, Levey-Jennings chart, review, non-conformities | complete |
| Inventory and equipment screens — lots, quarantine release, recalls, maintenance, calibration | complete |
| User and role administration, security log | complete |
| Patient registration, editing, coverage, consent, alerts, merge, anonymisation | complete |
| Order file — add analysis, cancel, repeat, correct the header | complete |
| Serum bank — shelving, aliquoting, discarding, purging | complete |
| Analysis catalogue — analyses, parameters, intervals, tariffs, panels, reflex rules, nomenclature | complete |
| Report register — reprint the stored original, record delivery, issue an amendment | complete |
| Analyser interfacing — host, message sink, mapping editor, message log, replay | complete |
| Third-party payment — claimable invoices, batch submission, outcomes, chasing | complete |
| Quality documents — register, versions, review, approval, entry into force, acknowledgement | complete |
| External quality assessment — rounds, submissions, Z-score and bias, automatic non-conformity | complete |
| Purchase orders — lines, approval by a second person, sending, receipt straight into stock | complete |
| Activity statistics — volumes, turnaround, on-time rate, money, by department/test/payer/day, CSV | complete |
| Test suite — QC rules, pricing, formulas, regulatory gates, protocols, amount-in-words, statistics, documents, purchase orders | 398 passed |

## Gap register — what a user cannot reach

An entry-point audit was run across all seven projects: for every permission, aggregate and service
method, is there a path from the UI to it? The result is below, because a module whose domain and
persistence exist but which nothing can call is **not** a finished module, and earlier versions of
this table said otherwise.

The audit is now mechanised as `scripts/check-reachability.ps1` and passes, so this register is
checkable rather than a claim: 0 uncalled service methods, 22 views all registered and navigable,
15 query interfaces all implemented and registered, 17 services all in the container.

### Fixed

- **Patient registration** — service, editor, coverage, consent, merge, anonymisation. This also
  lifted a deadlock: consent-gated analyses (genetic, HIV, prenatal) were unregisterable because
  nothing could record a consent.
- **Site legal identity, document numbering and settings** (`SettingsView`). This was the
  showstopper: the seeder creates the site with placeholder text and no ICE, IF, ministry
  authorisation or responsible biologist, and every setter on `LabSite` was dead — so
  `ValidateForInvoicing` and `ValidateForOperation` failed permanently and **no invoice could be
  issued and no report released on any installation**. The screen names exactly what is still
  outstanding in two banners rather than letting the administrator discover it one refusal at a
  time. Numbering formats are editable only until the first number is allocated, then frozen.

- **Correction paths (partly).** Reachable from the UI now: withdraw a lot rejection (returns to
  quarantine, not to service), reopen a non-conformity, dismiss an unfounded report, start and
  cancel a corrective action, activate and deactivate a role. New domain methods:
  `NonConformity.Reopen`, `NonConformity.Dismiss`, `ReagentLot.ClearRejection`.
- **A data-corruption bug.** `Invoice` was loaded without its lines or payments, so
  `RecordPayment` → `Recalculate()` summed an empty line list and **zeroed the totals of an issued
  invoice on every payment**. Fixed at the root with `InvoiceRepository`.

  Reachable now: refund a payment, mark a cheque returned unpaid, record a cash withdrawal, record
  a payer's claim response (the claim status no longer freezes at `Submitted`), retract a biological
  validation, and choose the Westgard rules per control.

- **Master data** (`MasterDataView`, five tabs). `Instrument`, `QcMaterial`, `QcTarget`,
  `StockItem`, `Supplier` and `Prescriber` were constructible only by the seeder or not at all,
  which left three finished screens opening onto permanently empty grids. The instrument tab also
  **re-arms a safety control**: `Instrument.CanProduceResults` refuses to let an analyser produce
  patient results on a lapsed calibration, but "lapsed" is computed from an interval nothing could
  set — so the interval was always null and the guard could never fire. The service now refuses to
  save an analyser without one.

- **Serum bank** (`SampleStorageView`). `MoveToStorageAsync`, `DiscardAsync` and
  `CreateAliquotAsync` had no caller, so a received tube could never be shelved, never aliquoted and
  never discarded: the bank filled up and nothing could be purged. The view lists everything
  received and not yet discarded — deliberately including the tubes that were **never shelved**,
  since an unshelved tube is precisely the one nobody can find when an add-on request arrives. Purge
  is a single pass over everything past retention against one recorded reason, because a purge that
  demands eighty individual confirmations is a purge that stops being done.

- **Invoice discount** (`InvoiceIssueDialog`). `Invoice.ApplyDiscount` and the
  `discountAmount` / `discountReason` parameters of `IssueInvoiceAsync` were unreachable from the
  UI. The confirmation dialog now carries them, with the net computed live — a discount is agreed as
  a final amount ("make it 300"), not as a reduction to be worked out backwards. The block is
  **absent**, not disabled, without `Billing.ApplyDiscount`.

- **The order after registration** (`OrderDetailView`, `OrderAmendmentService`). There was no order
  screen at all: `LabOrder.Cancel` and `LabOrderItem.Repeat` had no caller, and nothing could add an
  analysis to an existing file or correct its prescriber. The only recourse open to an operator was
  to register a second order — which splits one episode of care across two file numbers, bills the
  draw twice, and makes the turnaround figures fiction. Reachable now: add analyses, cancel one,
  cancel the file, repeat an analysis, correct the header.

  Two things worth naming. **The shared composition path**: resolution, the consent and fasting
  gates, pricing, tubes and the awaited result rows moved into `OrderComposer`, which both
  registration and amendment now use. An addition that bypassed the consent gate would be a genetic
  or HIV test performed without the written consent that has to pre-date the act, and an addition
  priced by a second implementation would price the same analysis differently from one ordered five
  minutes earlier. **Tube reuse**: an added analysis attaches to a tube already drawn for the same
  container rather than provoking a second needle, and the amendment succeeds with a warning when
  that tube is already in the laboratory — whether there is serum left is a question only the person
  holding it can answer.

- **The analysis catalogue** (`CatalogView`, `CatalogMaintenanceService`). Until this existed the
  catalogue was whatever the seeder wrote: `AnalysisTest`, `TestParameter`, `ReferenceRange`,
  `AnalysisTestTariff`, `TestPanel`, `ReflexRule`, `Department`, `SpecimenType`, `ContainerType` and
  `MeasurementUnit` were all constructible only by seed data, so a laboratory could not add an
  analysis, correct a reference interval or enter a tariff revision without a developer and a
  redeployment — and neither is exceptional: ISO 15189 §5.5.2 obliges periodic review of the
  intervals, and the national reference tariff is revised by decree.

  Four rules run through the service, and all four are about not falsifying the past. A **code** is
  never renamed — worklists, instrument mappings and every stored result key on it. A **value type**
  freezes once a result exists, because reading an old numeric column as a coded one makes the
  history unreadable. A **tariff** is versioned, never overwritten, so an invoice reprints with the
  price it charged. A **reference interval** in force is closed and superseded rather than edited,
  so a result keeps the interval it was interpreted against — and the screen tells the operator that
  a revision was opened rather than letting them find a second row and assume a bug.

  Three columns on the screen carry a warning rather than a value, all of the same class as the
  calibration-interval defect above: an analysis that cannot be ordered (no tariff or no active
  parameter), a parameter that cannot be completed (a coded picker with no options), and a reflex
  rule whose condition can never be met. All three look configured until somebody relies on them.
  Two fields also got the pickers they were missing — the subcontractor site and the
  instrument on a method-dependent interval were both unsettable, so both were dead.

- **The report register** (`ReportRegisterView`). `GetStoredPdfAsync` and `RecordDeliveryAsync` had
  no caller, and their absence was one compounding defect rather than two missing conveniences.
  Unreachable `GetStoredPdfAsync` meant a reprint would have had to re-render the report against
  today's catalogue instead of returning the document issued — a different document bearing the
  same number. Unreachable `RecordDeliveryAsync` meant no report was ever marked delivered, and
  because the amendment guard tested exactly that flag, **re-releasing minted v2 and v3 of the same
  report with none of them marked as a rectification**.

  Both are now reachable, and the guard no longer depends on clerical bookkeeping: any reissue of a
  **final** report is refused without an amendment reason, whether or not a hand-over was recorded,
  because printing a report and giving it to a patient without ticking a box is routine. A *partial*
  report is deliberately exempt — it is superseded by the complete one when the file finishes, and
  calling that an amendment would make every ordinary two-stage release look like a correction.

  The register leads on what has been released and not handed over, critical results first
  (ISO 15189 §5.9 makes reaching a clinician with a panic value an obligation, not a courtesy).

- **A verification blind spot.** The resource checker matched `T("key")` and `Localizer["key"]` but
  not the `MasterDataEditor` field helpers, which take the key as an argument — so every label,
  choice, lookup and notice in the reference-data and catalogue editors was unchecked. Widening the
  patterns took the checked count from 1 120 to 1 315 keys and immediately surfaced four invented
  keys and five whole enum sections (`DeliveryChannel`, `ReportRecipientKind`, `DeliveryStatus`,
  `ReportCompleteness`, `ReportAmendmentKind`) that were missing from all three catalogues.

- **A compile error.** `DatabaseInitializer` called `DemoDataSeeder.SeedAsync`, which did not
  exist — CS0103, so the solution did not build at all. Written: four patients and four orders left
  at four different stages, taking their numbers from the real sequences (a demonstration order
  carrying a number the sequence has not issued would collide with the first real registration),
  never seeded by default, and marked so nobody mistakes a fictitious patient for a real one.

- **Analyser interfacing, end to end.** `MarocLIS.Instruments` held a complete ASTM E1381/E1394 and
  HL7-over-MLLP stack — drivers, transports, parsers, a simulator — and **nothing started it**: no
  DI registration, no hosted service, and no implementation of `IInstrumentMessageSink`. A
  laboratory running this application had a finished interfacing project and still typed every
  result in by hand.

  Written: the sink (`InstrumentMessageSink`), the background host (`InstrumentHostedService`), the
  live-state monitor, the configuration and supervision service, and the screen
  (`InstrumentInterfaceView`) with its mapping editor, message log and raw-frame viewer.

  Five rules govern the sink, each from a specific way an interface produces a wrong patient result.
  A specimen identifier matching no tube — or two — is **never guessed at**. A control specimen
  **never enters a patient file**; analysers transmit controls down the same link. An unmapped code
  is **never inferred from its name**; "NA" is sodium on one analyser and not-applicable on another.
  A validated result is **never overwritten**, and the refusal surfaces as an issue so the
  correction goes through the amendment path. And every value is attributed to a **named service
  account** with the from-instrument flag set, never to whichever operator happened to be signed in
  — the host refuses to start if that account is missing rather than borrowing a human's name.

  Two things this uncovered. `TestResult` could not express an analyser-censored value, so
  `EnterCensoredNumeric` was added: reporting "< 0,01" as a bare 0,01 asserts a precision the method
  does not have, and for a tumour marker that changes what a clinician does next. And the
  configuration section in `appsettings.json` already named `EnableInstrumentHost`, which the new
  options class now matches instead of inventing a second switch beside it.

- **Third-party payment, and a second showstopper.** `Invoice.BiologistInpe` was read by
  `SubmitClaimBatchAsync` — which refuses a claim without it whenever the payer requires one — and
  **written nowhere**. Every AMO scheme this application seeds requires one (CNSS, CNOPS, Tadamon,
  Achamil), so **no AMO claim could ever be submitted on any installation**: the same class of
  defect as the `LabSite` one, and this time it blocked the laboratory's cash. The invoice-issue
  path now stamps the site's responsible biologist's INPE — a standing appointment under loi 12-01,
  not whichever clerk happened to issue the invoice.

  `ClaimBatchView` is the screen that was missing around it. Submission was reachable from the
  invoice ledger, but nothing showed a batch afterwards, so a submitted invoice left the screens
  entirely and an unanswered batch or a rejected invoice was invisible until somebody reconciled a
  bank statement by hand. Three panes in the order the money moves: what is claimable with the
  blockers named per row, the batches with unanswered-and-overdue first, and the invoices inside the
  selected batch with refusals first. A batch is one payer's invoices only — a payer that receives a
  mixed batch rejects the batch, not the invoice.

- **The UI mock-up** (`docs/ui-mockup.html`) now covers all 22 screens rather than 14 — the order
  file, serum bank, report register, third-party payment, analysis catalogue, analyser interfaces,
  reference data and site settings were all missing. Its sidebar mirrors
  `MainShellForm.BuildNavigation()` key for key, so a screen added to one and not the other shows up
  as a discrepancy instead of being quietly absent; the file's own navigation and view map are
  checked against each other, and both are complete.

- **Bidirectional text in Arabic.** A recorded-but-unfixed finding: in a right-to-left paragraph the
  Unicode bidirectional algorithm lays out Latin and neutral runs relative to the paragraph, so a
  cell reading `5 mL` rendered as `mL 5` and `120/80` as `80/120`. Two of those are clinically
  wrong — a reversed blood pressure is a different blood pressure. Fixed in two places rather than
  at three hundred call sites: every grid cell whose value is entirely Latin or numeric is wrapped
  in a first-strong isolate (U+2066 … U+2069), and `ILocalizer.Format` isolates each Latin argument
  before substituting it into an Arabic template — which is the one boundary every composed message
  passes through. French and English output is byte-identical to before.

### Seeded but not editable

`Payer` and `LabSite`. `LabSite` is editable from the settings screen for the site's own legal
identity; adding a second site and maintaining the payer list are still seed-only.

### Written with no entry point at all

`ControlledDocument` / `DocumentVersion` (SOP version control), `EqaParticipation` (external quality
assessment), `PurchaseOrder`. Domain and EF mapping only — zero references from the application or
desktop layers.

`ReflexRule` and `InstrumentInterface` / `InstrumentTestMapping` / `InstrumentMessageLog` have left
this list: the reflex rules are configurable from the catalogue screen, and the instrument tables
are read and written by the interfacing host and its supervision screen.

### Unreachable service methods

None known. The last of them closed in this pass: `ReportReleaseService.RecordDeliveryAsync` and
`GetStoredPdfAsync` through the report register, `ValidationService.UnvalidateAsync` and
`QualityControlService.SetActiveRulesAsync` through the validation and QC screens,
`UserAdministrationService.SetRoleActiveAsync` through the role editor.

`OrderRegistrationService.RegisterAsync` now has the prescriber, discount, prescription-date and
prescription-scan dimensions reachable, and the fields the registration screen does not carry are
editable afterwards from the order file.

### Screens not written

Multi-site administration.

Quality documents, external quality assessment, purchase orders and statistics were written on
13 October 2026 (`QualityDocumentsView`, `EqaView`, `PurchaseOrderView`, `StatisticsView`, with
`DocumentControlService`, `EqaService`, `PurchaseOrderService` and `StatisticsService` behind them).
Two decisions in them are worth knowing:

- Drafting a document needs the new permission `quality.document.manage`; bringing it into force
  needs `quality.document.approve`. The biologist in charge and the quality manager receive both on
  the next start, through the role seeding.
- Receiving a purchase-order line asks for the lot number and expiry and creates the lot, in
  quarantine, in the same save, so a delivery is never typed twice.

---

The core clinical workflow — register patient → register order → collect → receive → enter →
technically validate → biologically validate → release report → record its delivery — is complete
end to end, as are billing, quality control, non-conformities, stock movements, equipment
interventions, user administration, the analysis catalogue and the analyser interfaces.
`docs/EXTENSIBILITY.md` walks through adding a screen.

## Prerequisites

- .NET 10 SDK
- SQL Server Express 2019 or later
- An Arabic Naskh font with full glyph coverage for PDF output — drop `Amiri-Regular.ttf` and
  `Amiri-Bold.ttf` (SIL OFL) into `src/MarocLIS.Reporting/Assets/Fonts/`

## Configuration

`src/MarocLIS.Desktop/appsettings.json`:

```json
{
  "Database": {
    "ConnectionString": "Server=LABSRV\\SQLEXPRESS;Database=MarocLIS;Integrated Security=true"
  },
  "PasswordPolicy": { "MinimumLength": 10, "ExpiryDays": 180, "MaxFailedAttempts": 5 },
  "OrderRegistration": { "MedicalRecordRetentionYears": 10, "SampleRetentionDays": 7 }
}
```

First run creates the schema and seeds reference data, including a single administrator:
`admin` / `Maroc-LIS-2026!`, which **must** change its password before it can do anything.

## Before going live — figures the laboratory must set

The seed deliberately ships placeholders rather than guesses, because a wrong value here is a
compliance failure rather than a bug:

1. **Letter-key value (`B`)** — `ReferenceDataSeeder.PlaceholderLetterKeyValueMad` is `1.00`. The AMO
   reimbursement base is `coefficient × letter-key value`; set it from the current Tarif National de
   Référence.
2. **Act coefficients** — the coefficients in `StarterCatalogue` are plausible placeholders. Confirm
   each against the official nomenclature.
3. **Reference intervals** — every seeded interval is marked
   `Source = "À vérifier par le laboratoire"`. ISO 15189 §5.5.2 requires the laboratory to establish
   and periodically review its own.
4. **Site legal identity** — ICE, IF, RC, professional tax, CNSS, INPE and the Ministry of Health
   authorisation number. `LabSite.ValidateForInvoicing()` blocks invoicing until they are present.
5. **Responsible biologist** — `LabSite.ValidateForOperation()` blocks operation without one.

## Regulatory basis

See [`docs/REGLEMENTATION-MAROC.md`](docs/REGLEMENTATION-MAROC.md) for the clause-by-clause mapping
of requirements to the code that implements them. In outline:

- **Loi n° 12-01** — medical biology laboratories: ministry authorisation, responsible biologist,
  biologist-only release of results.
- **Loi n° 08-09** and the CNDP — personal data: consent, access logging, retention, right to
  erasure by pseudonymisation.
- **ISO 15189** — pre-analytical traceability, internal QC and EQA, non-conformities, document
  control, equipment and reagent-lot traceability, critical-result communication with read-back.
- **DGI / Code général des impôts** — invoice mentions (ICE, IF, RC, professional tax), unbroken
  chronological numbering, correction by credit note only, VAT exemption for biology acts,
  cash-payment threshold warning.
- **AMO (CNSS, CNOPS, Tadamon, Achamil)** — TNR-based pricing, payer/patient split, ALD prior
  agreements, INPE on claims.

This is a software implementation of the requirements as understood; it is not legal advice, and the
laboratory's quality manager and accountant should review the mapping document before go-live.
