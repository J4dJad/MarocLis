# PDF library licensing — decision required before production

The report renderer is implemented against **QuestPDF**, and that choice has a licence implication
that has to be settled by someone with commercial authority, not by the development team.

## The constraint

QuestPDF is dual-licensed:

| Licence | Who may use it |
|---|---|
| **Community MIT** | Individuals, and organisations whose **annual gross revenue is below 1 M USD** |
| **Professional / Enterprise** (paid, per-developer) | Everyone else |

Cognizant is far above the Community threshold. **If this software is developed or distributed by
Cognizant, the Community licence does not apply** and a paid QuestPDF licence is required. The same
question applies to the end customer if they redistribute.

`QuestPdfReportRenderer` sets `QuestPDF.Settings.License` from configuration rather than hard-coding
`LicenseType.Community`, precisely so this is a deliberate, recorded decision instead of a default
somebody inherited.

## Why QuestPDF was chosen anyway

Arabic is the deciding factor. A Moroccan laboratory report has to render Arabic correctly, and that
means **complex text shaping**: contextual letter forms (initial / medial / final / isolated),
mandatory ligatures such as لا, and correct bidirectional ordering when Latin tokens like `mg/dL`
sit inside Arabic text. Get it wrong and the report is not merely ugly — it is unreadable or, worse,
subtly wrong.

QuestPDF renders through SkiaSharp with HarfBuzz shaping, which handles this properly. Most of the
free .NET alternatives do not.

## Alternatives, with the trade-off stated honestly

| Option | Licence | Arabic shaping | Verdict |
|---|---|---|---|
| **QuestPDF** | Paid above 1 M USD revenue | ✅ HarfBuzz via SkiaSharp | Current implementation. Best developer experience; costs money here |
| **PDFsharp / MigraDoc** | MIT — free commercially | ❌ No complex shaping; Arabic renders as disconnected isolated glyphs | Not viable for Arabic output |
| **Headless Chromium** (PuppeteerSharp + HTML/CSS template) | Apache 2.0 — free | ✅ Full browser text engine, best in class | Viable. Costs a ~150 MB Chromium per workstation and a process launch per report; HTML/CSS templates are also easier for a laboratory to restyle |
| **iText 7** | AGPL, or paid commercial | ✅ | AGPL is unacceptable for a closed deployment; the commercial licence costs more than QuestPDF |
| **Aspose.PDF / Syncfusion** | Paid | ✅ | Works; more expensive |

## How to switch

The application layer depends on **`IReportRenderer`** (`MarocLIS.Application.Reports`), not on
QuestPDF. Swapping means writing one class and changing one DI registration:

```csharp
services.AddSingleton<IReportRenderer, ChromiumHtmlReportRenderer>();
```

`ReportModel` is a flat, immutable projection with no rendering concepts in it, so it feeds an HTML
template as readily as a QuestPDF document.

## Recommendation

1. **If a QuestPDF Professional licence is affordable** — buy it. It is per-developer, one-off, and
   the code is already written.
2. **If not** — implement `ChromiumHtmlReportRenderer` against the same interface. Budget roughly
   two to three days including the Arabic RTL template, and accept the Chromium footprint on each
   workstation.

Do not ship with `LicenseType.Community` set while the revenue threshold is exceeded. That is a
licence violation, and it is the kind that surfaces during due diligence.
