using System.Globalization;
using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using MarocLIS.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The browser's date and time box: what is typed or picked is what is kept (bound with a format,
/// the box was put back to the time shown), written and read the browser's way in every language.
/// </summary>
public sealed class BrowserDateTimeTests
{
    private static readonly DateTime Shown = new(2026, 10, 13, 14, 30, 0);

    [Theory]
    [InlineData("2026-10-12T07:30", 2026, 10, 12, 7, 30)]
    [InlineData("2026-10-12T07:30:45", 2026, 10, 12, 7, 30)]
    [InlineData("2026-10-12T07:30:45.123", 2026, 10, 12, 7, 30)]
    public void TheTimeSentByTheBrowser_IsKept_ToTheMinute(string sent, int y, int mo, int d, int h, int mi) =>
        BrowserDateTime.Read(new ChangeEventArgs { Value = sent }, Shown).Should().Be(new DateTime(y, mo, d, h, mi, 0));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("demain")]
    public void AClearedOrUnreadableBox_KeepsTheTimeShown(string? sent) =>
        BrowserDateTime.Read(new ChangeEventArgs { Value = sent }, Shown).Should().Be(Shown);

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void TheBox_IsWrittenAndReadTheBrowsersWay_WhateverTheScreensLanguage(AppLanguage language)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureManager.Build(language);
            var written = BrowserDateTime.Write(new DateTime(2026, 10, 12, 7, 5, 59));
            written.Should().Be("2026-10-12T07:05");
            BrowserDateTime.Read(new ChangeEventArgs { Value = written }, Shown).Should().Be(new DateTime(2026, 10, 12, 7, 5, 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
