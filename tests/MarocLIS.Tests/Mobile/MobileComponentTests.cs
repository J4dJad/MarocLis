using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using MarocLIS.Api.Contracts;
using MarocLIS.Mobile.Components;
using MarocLIS.Mobile.Layout;
using MarocLIS.Mobile.Pages;
using MarocLIS.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Mobile;

/// <summary>
/// The phone's screens rendered in memory: which rows of a file can be ticked, what « Valider »
/// sends and what a conflict does, the justification's counter, and the right-to-left layout of the
/// Arabic screens. The API is a stub answering what the real one would.
/// </summary>
public sealed class MobileComponentTests : BunitContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly StubApi _api = new();

    public MobileComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<bool>("marocLis.watchConnectivity", _ => true).SetResult(true);
        Services.AddScoped(_ => new HttpClient(_api) { BaseAddress = new Uri("https://lis.example/") });
        Services.AddMobileServices();
    }

    private static readonly Guid OrderId = Guid.NewGuid();

    private static ResultRow Row(string name, string value, bool canTick = true, bool critical = false,
                                 bool communicated = false, string status = "TechnicallyValidated",
                                 string interpretation = "Normal", params string[] flags) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "IONO", "Ionogramme", name, false, 0, value, "mmol/L", "3,5 - 5,1",
            interpretation, flags, "", interpretation != "Normal", null, null, false, null, critical, communicated,
            status, new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.FromHours(1)), canTick, canTick);

    private static OrderFile File(params ResultRow[] rows) =>
        new(OrderId, "261017-0042", new PatientSummary("EXEMPLE Patiente", null, "Female", 64, null, false),
            "Urgent", null, null, true, [], rows);

    private void SignIn()
    {
        Services.GetRequiredService<SessionState>().SignedIn(
            new SessionInfo("dr.exemple", "Dr Exemple", "MedicalBiologist", "A", "French", 15, "Mobile · test"));
    }

    // ---- Rows ------------------------------------------------------------------------------------

    [Fact]
    public void OrderPage_TicksWhatTheServerAllows_ButNeitherAnUncalledCriticalNorAValueAwaitingTheTechnician()
    {
        var sodium = Row("Sodium", "134", interpretation: "Low");
        var potassium = Row("Potassium", "6,9", critical: true, interpretation: "CriticalHigh");
        var crp = Row("CRP", "48,2", canTick: false, status: "Entered");
        var chloride = Row("Chlore", "101");
        _api.Answer($"/api/dossiers/{OrderId}", File(sodium, potassium, crp, chloride));
        SignIn();

        var page = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        page.WaitForElement(".result-row");

        var boxes = page.FindAll(".result-row input[type=checkbox]");
        boxes.Select(b => b.HasAttribute("disabled")).Should().Equal(false, true, true, false);
        boxes.Select(b => b.HasAttribute("checked")).Should().Equal(true, false, false, true);

        // The counter and the button count what can go, not what is shown.
        page.Find(".card-head").TextContent.Should().Contain("2 sur 2");
        page.Find(".bottombar .btn-primary").TextContent.Should().Contain("(2)");
        page.Find(".critical-banner").Should().NotBeNull();
        page.Find(".bottombar .hint").TextContent.Should().Contain("Potassium");

        // Once the call is recorded, the server says so and the row can be ticked like any other.
        _api.Answer($"/api/dossiers/{OrderId}", File(sodium, potassium with { CriticalCommunicated = true }, crp, chloride));
        var reloaded = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        reloaded.WaitForElement(".result-row");
        reloaded.FindAll(".result-row input[type=checkbox]")[1].HasAttribute("disabled").Should().BeFalse();
        reloaded.FindAll(".critical-banner").Should().BeEmpty();
    }

    [Fact]
    public void OrderPage_SendsTheEntryTimeOfWhatWasShown_AndOnAConflictSaysSoAndReadsTheFileAgain()
    {
        var glucose = Row("Glycémie", "1,04");
        _api.Answer($"/api/dossiers/{OrderId}", File(glucose));
        _api.Answer($"/api/dossiers/{OrderId}/validation", HttpStatusCode.Conflict,
            """{"title":"Conflit","status":409,"errors":[{"code":"Conflict","key":"Validation_ResultChangedSinceDisplayed","message":"Glycémie a été modifié depuis son affichage."}]}""");
        SignIn();

        var page = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        page.WaitForElement(".result-row");
        var opened = _api.Count($"/api/dossiers/{OrderId}");

        page.Find(".bottombar .btn-primary").Click();

        page.WaitForAssertion(() => page.Find(".message").TextContent.Should().Contain("modifié depuis son affichage"));
        _api.Count($"/api/dossiers/{OrderId}").Should().Be(opened + 1, "the file is read again after a conflict");

        using var sent = JsonDocument.Parse(_api.LastBody($"/api/dossiers/{OrderId}/validation")!);
        var decision = sent.RootElement.GetProperty("decisions")[0];
        decision.GetProperty("resultId").GetGuid().Should().Be(glucose.ResultId);
        decision.GetProperty("displayedEnteredAt").GetDateTimeOffset().Should().Be(glucose.EnteredAt!.Value);
    }

    // ---- Dérogation -----------------------------------------------------------------------------------

    [Fact]
    public void DerogationSheet_CountsTheTrimmedJustification_AndConfirmsBetween15And1000Characters()
    {
        var glucose = Row("Glycémie", "1,32", interpretation: "High", flags: "QcOutOfControl");
        var sheet = Render<DerogationSheet>(p => p
            .Add(x => x.File, File(glucose))
            .Add(x => x.Rows, [glucose]));

        var confirm = () => sheet.Find(".sheet-actions .btn-primary");
        sheet.Find(".field-row .num").TextContent.Should().Be("0 / 1000");
        confirm().HasAttribute("disabled").Should().BeTrue();

        sheet.Find("textarea").Input("   trop court   ");
        sheet.Find(".field-row .num").TextContent.Should().Be("10 / 1000");
        confirm().HasAttribute("disabled").Should().BeTrue();

        sheet.Find("textarea").Input("Contrôle niveau 2 repassé et conforme.");
        sheet.Find(".field-row .num").TextContent.Should().Be("38 / 1000");
        confirm().HasAttribute("disabled").Should().BeFalse();

        sheet.Find("textarea").Input(new string('x', 1001));
        sheet.Find(".field-row .num").ClassList.Should().Contain("over");
        confirm().HasAttribute("disabled").Should().BeTrue();
    }

    // ---- Languages ------------------------------------------------------------------------------------

    [Fact]
    public void Layout_IsRightToLeftInArabic_WithAccessionNumbersAndValuesKeptLeftToRight()
    {
        _api.Answer(ApiProtocol.SessionRoute, HttpStatusCode.Unauthorized, """{"title":"Non connecté","status":401}""");
        var texts = Services.GetRequiredService<Texts>();

        var layout = Render<MainLayout>();
        layout.WaitForAssertion(() => layout.Find(".app").GetAttribute("dir").Should().Be("ltr"));

        texts.Use(UiLanguage.Arabic);
        layout.WaitForAssertion(() =>
        {
            layout.Find(".app").GetAttribute("dir").Should().Be("rtl");
            layout.Find(".app").GetAttribute("lang").Should().Be("ar");
        });
        // The whole document follows, not only the application's frame.
        JSInterop.Invocations["marocLis.setDocumentLanguage"][^1].Arguments.Should().Equal("ar", "rtl");

        // In a file, the accession number and the values stay left to right inside the Arabic page.
        var potassium = Row("البوتاسيوم", "6,9", interpretation: "High");
        _api.Answer($"/api/dossiers/{OrderId}", File(potassium));
        SignIn();
        var page = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        page.WaitForElement(".result-row");
        page.Find(".topbar-title").ClassList.Should().Contain("ltr");
        page.Find(".result-value").ClassList.Should().Contain("ltr");
        page.Find(".bottombar .btn-primary").TextContent.Should().NotContain("Valider", "the screen is in Arabic");
    }

    [Fact]
    public void SignIn_OffersOnlyTheLaboratorysLanguages_AndLeavesOneSwitchedOffForFrench()
    {
        _api.Answer(ApiProtocol.LanguagesRoute, new LanguagesInfo(["French", "English"]));
        var texts = Services.GetRequiredService<Texts>();
        texts.Use(UiLanguage.Arabic);

        var page = Render<SignIn>();

        page.WaitForAssertion(() =>
            page.FindAll(".languages button").Select(b => b.GetAttribute("lang")).Should().Equal("fr", "en"));
        texts.Language.Should().Be(UiLanguage.French, "Arabic is no longer one of the laboratory's languages");

        // French alone: nothing to choose, so no buttons at all.
        _api.Answer(ApiProtocol.LanguagesRoute, new LanguagesInfo(["French"]));
        var frenchOnly = Render<SignIn>();
        frenchOnly.WaitForAssertion(() => frenchOnly.FindAll(".languages").Should().BeEmpty());
    }

    [Fact]
    public void AccountSheet_OffersOnlyTheLanguagesTheSessionSays()
    {
        Services.GetRequiredService<SessionState>().SignedIn(
            new SessionInfo("dr.exemple", "Dr Exemple", "MedicalBiologist", "A", "French", 15, "Mobile · test",
                            ActiveLanguages: ["French", "Arabic"]));

        var sheet = Render<AccountSheet>();

        sheet.FindAll(".segment").Select(s => s.GetAttribute("lang")).Should().Equal("fr", "ar");
    }

    /// <summary>The API as a dictionary of answers, recording what the phone sent.</summary>
    private sealed class StubApi : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = [];
        private readonly Dictionary<string, int> _counts = [];
        private readonly Dictionary<string, string?> _bodies = [];

        public void Answer(string path, object value) => _answers[path] = (HttpStatusCode.OK, JsonSerializer.Serialize(value, Web));

        public void Answer(string path, HttpStatusCode status, string json) => _answers[path] = (status, json);

        public int Count(string path) => _counts.GetValueOrDefault(path);

        public string? LastBody(string path) => _bodies.GetValueOrDefault(path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            _counts[path] = Count(path) + 1;
            _bodies[path] = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            request.Headers.GetValues(ApiProtocol.RequestHeader).Should().ContainSingle(ApiProtocol.RequestHeaderValue);

            var (status, body) = _answers.TryGetValue(path, out var answer)
                ? answer
                : (HttpStatusCode.NotFound, """{"title":"Introuvable","status":404}""");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    (int)status < 300 ? "application/json" : "application/problem+json")
            };
        }
    }
}
