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

    // ---- Prescripteur -----------------------------------------------------------------------------------

    private static readonly PrescriberSummary Alaoui = new(
        "ALAOUI Karim", "Néphrologie", "Casablanca", CriticalLine: "0522 11 22 33", Phone: "0522 00 00 00",
        Mobile: null, Email: "k.alaoui@example.ma", Fax: "0522 00 00 01", CriticalContact: "0522 11 22 33");

    [Fact]
    public void OrderPage_FoldsThePrescriberToItsName_AndUnfoldsItWhenACriticalValueIsWaitingForTheCall()
    {
        var sodium = Row("Sodium", "134", interpretation: "Low");
        _api.Answer($"/api/dossiers/{OrderId}", File(sodium) with { Prescriber = Alaoui });
        SignIn();

        var page = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        page.WaitForElement(".prescriber");

        page.Find(".prescriber-name").TextContent.Should().Be("ALAOUI Karim");
        page.Find(".prescriber-toggle").GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll(".contact-row").Should().BeEmpty();

        page.Find(".prescriber-toggle").Click();
        var rows = page.FindAll(".contact-row");
        rows.Select(r => r.QuerySelector(".contact-label")!.TextContent)
            .Should().Equal("Ligne valeurs critiques", "Tél.", "E-mail", "Fax");
        rows[0].ClassList.Should().Contain("critical");
        rows[0].QuerySelector("a")!.GetAttribute("href").Should().Be("tel:0522112233");
        rows[2].QuerySelector("a")!.GetAttribute("href").Should().Be("mailto:k.alaoui@example.ma");
        rows[3].QuerySelector("a").Should().BeNull("a fax is copied, not dialled");
        page.Find(".prescriber-speciality").TextContent.Should().Be("Néphrologie · Casablanca");

        // An uncalled critical value: the card opens by itself on the numbers.
        var potassium = Row("Potassium", "6,9", critical: true, interpretation: "CriticalHigh");
        _api.Answer($"/api/dossiers/{OrderId}", File(potassium) with { Prescriber = Alaoui });
        var critical = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        critical.WaitForElement(".prescriber");
        critical.Find(".prescriber-toggle").GetAttribute("aria-expanded").Should().Be("true");
        critical.FindAll(".contact-row").Should().HaveCount(4);

        // Without a prescriber, no card.
        _api.Answer($"/api/dossiers/{OrderId}", File(sodium));
        var none = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        none.WaitForElement(".result-row");
        none.FindAll(".prescriber").Should().BeEmpty();
    }

    [Fact]
    public void PrescriberCard_CopiesAValueThroughTheBrowser_AndSaysSo()
    {
        var copy = JSInterop.Setup<bool>("marocLis.copyText", _ => true);
        copy.SetResult(true);

        var card = Render<PrescriberCard>(p => p.Add(x => x.Prescriber, Alaoui).Add(x => x.Expanded, true));
        card.FindAll(".contact-copy")[1].Click();

        copy.Invocations.Should().ContainSingle().Which.Arguments.Should().Equal("0522 00 00 00");
        card.WaitForAssertion(() => card.Find(".contact-copied").TextContent.Should().Be("Copié"));
        card.FindAll(".contact-copied").Should().ContainSingle();
    }

    [Fact]
    public void CriticalSheet_NamesWhomToCall_AndSendsTheNumberTappedOnTheFile_ElseLeavesTheServersDefault()
    {
        var potassium = Row("Potassium", "6,9", critical: true, interpretation: "CriticalHigh");
        var file = File(potassium) with { Prescriber = Alaoui };
        var route = $"/api/critiques/{potassium.ResultId}";

        var tapped = Render<CriticalSheet>(p => p.Add(x => x.File, file).Add(x => x.Row, potassium)
                                                 .Add(x => x.Dialled, "0522 00 00 00"));
        tapped.Find(".call-to").TextContent.Should().Contain("ALAOUI Karim").And.Contain("0522 00 00 00");
        tapped.Find(".call-to a").GetAttribute("href").Should().Be("tel:0522000000");
        tapped.Find("#recipient").Input("Dr Alaoui");
        tapped.Find("#readback").Input("6,9");
        tapped.Find(".sheet-actions .btn-primary").Click();

        tapped.WaitForAssertion(() => _api.LastBody(route).Should().NotBeNull());
        using (var sent = JsonDocument.Parse(_api.LastBody(route)!))
            sent.RootElement.GetProperty("contactUsed").GetString().Should().Be("0522 00 00 00");

        var offered = Render<CriticalSheet>(p => p.Add(x => x.File, file).Add(x => x.Row, potassium));
        offered.Find(".call-to").TextContent.Should().Contain("0522 11 22 33", "the critical line, which the server records");
        offered.Find("#recipient").Input("Dr Alaoui");
        offered.Find("#readback").Input("6,9");
        offered.Find(".sheet-actions .btn-primary").Click();

        offered.WaitForAssertion(() => _api.Count(route).Should().Be(2));
        using var defaulted = JsonDocument.Parse(_api.LastBody(route)!);
        defaulted.RootElement.GetProperty("contactUsed").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void OrderPage_HandsTheCriticalSheetTheNumberTappedOnTheCard_AndTheCriticalLineOtherwise()
    {
        var potassium = Row("Potassium", "6,9", critical: true, interpretation: "CriticalHigh");
        _api.Answer($"/api/dossiers/{OrderId}", File(potassium) with { Prescriber = Alaoui });
        var route = $"/api/critiques/{potassium.ResultId}";
        SignIn();

        var page = Render<OrderPage>(p => p.Add(x => x.OrderId, OrderId));
        page.WaitForElement(".prescriber");

        // Nothing tapped: the critical line. A string parameter written without « @ » passed the
        // field's name, so the sheet showed no number and sent « _dialled » as the contact used.
        page.Find(".btn-critical").Click();
        page.WaitForElement(".call-to a");
        page.Find(".call-to a").GetAttribute("href").Should().Be("tel:0522112233");
        page.Find(".sheet-actions .btn-grow:last-child").Click();
        page.WaitForAssertion(() => page.FindAll(".call-to").Should().BeEmpty());

        // The telephone tapped on the card: shown, and sent with the call.
        page.FindAll(".contact-row")[1].QuerySelector("a")!.Click();
        page.Find(".btn-critical").Click();
        page.WaitForElement(".call-to a");
        page.Find(".call-to").TextContent.Should().Contain("0522 00 00 00");
        page.Find("#recipient").Input("Dr Alaoui");
        page.Find("#readback").Input("6,9");
        page.Find(".sheet-actions .btn-primary").Click();

        page.WaitForAssertion(() => _api.LastBody(route).Should().NotBeNull());
        using var sent = JsonDocument.Parse(_api.LastBody(route)!);
        sent.RootElement.GetProperty("contactUsed").GetString().Should().Be("0522 00 00 00");
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
