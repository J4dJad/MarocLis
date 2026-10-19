using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The trial mark « VERSION D'ESSAI – NE PAS REMETTRE AU PATIENT » across every report page under a
/// trial or training licence, and nowhere under a paid one. The pages are drawn and their red
/// pixels counted: the mark is the only red on a report whose results are all normal.
/// </summary>
public sealed class TrialMarkRendererTests
{
    /// <summary>Set to a folder, the marked page is written there too, for a look at it.</summary>
    private const string OutputVariable = "MAROCLIS_TRIALMARK_PNG";

    private static readonly ReportingOptions Options = new()
    {
        FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
    };

    private static readonly JsonLocalizer Localizer = new(new LocalizationOptions { MarkMissingKeys = true });

    private static LicenceStatus Trial() => FullStatus() with { IsMarked = true };

    [Fact]
    public void AReportUnderATrialLicence_CarriesTheMark_OnEveryPage()
    {
        var plain = new QuestPdfReportRenderer(Options, Localizer, new FixedLicenceState(FullStatus())).RenderPageImages(Model());
        var marked = new QuestPdfReportRenderer(Options, Localizer, new FixedLicenceState(Trial())).RenderPageImages(Model());

        marked.Should().HaveSameCount(plain);
        RedPixels(plain[0]).Should().Be(0, "a report of normal results has no red of its own");
        foreach (var page in marked) RedPixels(page).Should().BeGreaterThan(2_000);

        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, "rapport-version-essai.png"), marked[0]);
    }

    [Fact]
    public void WithoutALicenceState_TheReportIsNotMarked()
    {
        var page = new QuestPdfReportRenderer(Options, Localizer).RenderPageImages(Model())[0];

        RedPixels(page).Should().Be(0);
    }

    [Fact]
    public void TheMark_IsInTheReportsLanguage()
    {
        const string French = "VERSION D'ESSAI – NE PAS REMETTRE AU PATIENT";
        Localizer.Get("Licence.TrialMark", AppLanguage.French).Should().Be(French);
        Localizer.Get("Licence.TrialMark", AppLanguage.Arabic).Should().NotBe(French).And.NotContain("[");
    }

    [Fact]
    public void AnInvoiceUnderATrialLicence_IsMarkedToo()
    {
        var plain = new QuestPdfInvoiceRenderer(Options, null, new FixedLicenceState(FullStatus()));
        var marked = new QuestPdfInvoiceRenderer(Options, null, new FixedLicenceState(Trial()));

        // A PDF's text is not read back here; the marked one draws a page layer the other has not.
        marked.RenderInvoice(Billing.InvoiceRendererTests.Sample(), AppLanguage.French).Length
            .Should().BeGreaterThan(plain.RenderInvoice(Billing.InvoiceRendererTests.Sample(), AppLanguage.French).Length);
    }

    private static int RedPixels(byte[] png)
    {
        var (width, height, rgba) = Png.Decode(png);
        var count = 0;
        for (var y = 0; y < height; y += 2)
        for (var x = 0; x < width; x += 2)
        {
            var i = (y * width + x) * 4;
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            if (r > 180 && g < 170 && b < 170 && r - g > 50) count++;
        }

        return count;
    }

    /// <summary>
    /// Just enough of a PNG reader for the pages QuestPDF draws (8-bit RGB or RGBA, not interlaced):
    /// SkiaSharp's native library is not there on every machine the tests run on.
    /// </summary>
    private static class Png
    {
        public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
        {
            int width = 0, height = 0, channels = 0;
            using var idat = new MemoryStream();
            for (var at = 8; at < png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
                var type = Encoding.ASCII.GetString(png, at + 4, 4);
                var data = png.AsSpan(at + 8, length);
                if (type == "IHDR")
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    data[8].Should().Be(8, "an 8-bit page");
                    channels = data[9] switch { 2 => 3, 6 => 4, var other => throw new NotSupportedException($"colour type {other}") };
                    data[12].Should().Be(0, "not interlaced");
                }
                else if (type == "IDAT") idat.Write(data);

                at += 12 + length;
            }

            idat.Position = 0;
            using var inflated = new MemoryStream();
            using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(inflated);
            var raw = inflated.ToArray();

            var stride = width * channels;
            var rows = new byte[height * stride];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                for (var x = 0; x < stride; x++)
                {
                    var value = raw[y * (stride + 1) + 1 + x];
                    int left = x >= channels ? rows[y * stride + x - channels] : 0;
                    int up = y > 0 ? rows[(y - 1) * stride + x] : 0;
                    int corner = x >= channels && y > 0 ? rows[(y - 1) * stride + x - channels] : 0;
                    rows[y * stride + x] = (byte)(filter switch
                    {
                        0 => value,
                        1 => value + left,
                        2 => value + up,
                        3 => value + (left + up) / 2,
                        4 => value + Paeth(left, up, corner),
                        _ => throw new NotSupportedException($"filter {filter}")
                    });
                }
            }

            var rgba = new byte[width * height * 4];
            for (var p = 0; p < width * height; p++)
            {
                rgba[p * 4] = rows[p * channels];
                rgba[p * 4 + 1] = rows[p * channels + 1];
                rgba[p * 4 + 2] = rows[p * channels + 2];
                rgba[p * 4 + 3] = channels == 4 ? rows[p * channels + 3] : (byte)255;
            }

            return (width, height, rgba);
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    private static ReportModel Model() => new()
    {
        LabOrderId = Guid.NewGuid(),
        AccessionNumber = "CASA-261018-0007",
        Language = AppLanguage.French,
        VersionNumber = 1,
        Completeness = ReportCompleteness.Final,
        LaboratoryName = "Laboratoire Al Amal",
        LaboratoryCity = "Casablanca",
        PatientName = "EL ALAOUI Fatima",
        MedicalRecordNumber = "P-2026-004512",
        Gender = Gender.Female,
        AgeYears = 42,
        PrescriberName = "Dr Bennani",
        RegisteredAt = new DateTimeOffset(2026, 10, 18, 8, 10, 0, TimeSpan.FromHours(1)),
        ReleasedAt = new DateTimeOffset(2026, 10, 18, 11, 40, 0, TimeSpan.FromHours(1)),
        SignatoryName = "Dr Amina Tazi",
        SignatoryQualification = "MedicalBiologist",
        SignatoryOrderNumber = "12345",
        Sections =
        [
            new ReportSection("BIO", new MultilingualText("Biochimie"), 1,
            [
                new ReportAnalysisBlock("GLY", new MultilingualText("Glycémie à jeun"), null, false, false,
                    new DateTimeOffset(2026, 10, 18, 11, 0, 0, TimeSpan.FromHours(1)),
                [
                    new ReportLine
                    {
                        ParameterCode = "GLY",
                        ParameterName = new MultilingualText("Glycémie"),
                        Value = "0,92",
                        Unit = "g/L",
                        ReferenceRange = "0,70 – 1,10",
                        Interpretation = ResultInterpretation.Normal
                    }
                ])
            ])
        ]
    };
}
