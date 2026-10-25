using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace MarocLIS.Web.Components.Shared;

/// <summary>
/// A date and time box of the browser (<c>type="datetime-local"</c>), written and read explicitly.
/// Bound with <c>@bind</c> and a format, the time typed or picked was put back to the one shown;
/// here the box shows <c>2026-10-12T14:30</c> whatever the screen's language and what the browser
/// sends back (with or without seconds) is read as it is.
/// </summary>
public static class BrowserDateTime
{
    /// <summary>The browser's own writing of a date and time, to the minute.</summary>
    public static string Write(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>What the box sent back, to the minute; a cleared or unreadable box keeps the time shown.</summary>
    public static DateTime Read(ChangeEventArgs e, DateTime shown)
    {
        ArgumentNullException.ThrowIfNull(e);
        return DateTime.TryParse(e.Value as string, CultureInfo.InvariantCulture, DateTimeStyles.None, out var read)
            ? new DateTime(read.Year, read.Month, read.Day, read.Hour, read.Minute, 0, DateTimeKind.Unspecified)
            : shown;
    }
}
