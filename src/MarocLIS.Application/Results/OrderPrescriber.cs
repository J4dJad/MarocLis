using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Application.Results;

/// <summary>
/// The order's prescriber as the validation screens show it: who to call about the file. Either a
/// prescriber of the directory (<see cref="PrescriberId"/> set, with the contact details recorded
/// for them) or the name typed on the order when the prescriber is not in it — a name only.
/// </summary>
public sealed record OrderPrescriber
{
    public Guid? PrescriberId { get; init; }
    public PrescriberKind Kind { get; init; }
    public string? LastName { get; init; }
    public string? FirstName { get; init; }
    public string? FullNameArabic { get; init; }
    public string? InstitutionName { get; init; }

    /// <summary>The name typed on the order, when the prescriber is not in the directory.</summary>
    public string? FreeText { get; init; }

    public MultilingualText? Speciality { get; init; }
    public string? CriticalResultPhone { get; init; }
    public string? Phone { get; init; }
    public string? MobilePhone { get; init; }
    public string? Email { get; init; }
    public string? Fax { get; init; }
    public string? City { get; init; }
}

/// <summary>A way of reaching the prescriber, in the order the screens list them.</summary>
public enum PrescriberContactKind
{
    /// <summary>« Ligne valeurs critiques » — the number to call for a critical result.</summary>
    CriticalLine = 0,
    Phone = 1,
    Mobile = 2,
    Email = 3,
    Fax = 4
}

public sealed record PrescriberContact(PrescriberContactKind Kind, string Value)
{
    /// <summary>A number that can be dialled: everything but the e-mail address.</summary>
    public bool IsPhoneNumber => Kind is not PrescriberContactKind.Email;
}

/// <summary>
/// What the workstation and the phone show of an order's prescriber, and which number a critical
/// call is recorded against. One rule for both, so the number on the screen is the number logged.
/// </summary>
public static class PrescriberContactRules
{
    /// <summary>
    /// The name shown: the establishment's for a clinic or a referring laboratory, as on the report
    /// register (ResultQueries), else the doctor's; the Arabic name in Arabic when there is one; the
    /// typed name for a prescriber outside the directory. Null when the order names nobody.
    /// </summary>
    public static string? DisplayName(OrderPrescriber? prescriber, AppLanguage language)
    {
        if (prescriber is null) return null;

        if (prescriber.PrescriberId is null)
            return Clean(prescriber.FreeText);

        if (language == AppLanguage.Arabic && Clean(prescriber.FullNameArabic) is { } arabic)
            return arabic;

        if (Prescriber.IsEstablishment(prescriber.Kind) && Clean(prescriber.InstitutionName) is { } institution)
            return institution;

        return Clean($"{prescriber.LastName} {prescriber.FirstName}") ?? Clean(prescriber.InstitutionName);
    }

    /// <summary>
    /// The recorded ways of reaching the prescriber, the critical line first; a missing value is
    /// left out. A free-text prescriber has none: only the name was typed.
    /// </summary>
    public static IReadOnlyList<PrescriberContact> Contacts(OrderPrescriber? prescriber)
    {
        if (prescriber?.PrescriberId is null) return [];

        var contacts = new List<PrescriberContact>(5);
        Add(PrescriberContactKind.CriticalLine, prescriber.CriticalResultPhone);
        Add(PrescriberContactKind.Phone, prescriber.Phone);
        Add(PrescriberContactKind.Mobile, prescriber.MobilePhone);
        Add(PrescriberContactKind.Email, prescriber.Email);
        Add(PrescriberContactKind.Fax, prescriber.Fax);
        return contacts;

        void Add(PrescriberContactKind kind, string? value)
        {
            if (Clean(value) is { } clean) contacts.Add(new PrescriberContact(kind, clean));
        }
    }

    /// <summary>
    /// The number a critical result is phoned to, and recorded as the contact used: the critical
    /// line when the prescriber has one, else the mobile, else the main number. Null when none is
    /// recorded.
    /// </summary>
    public static string? CriticalContact(OrderPrescriber? prescriber) =>
        prescriber?.PrescriberId is null
            ? null
            : Clean(prescriber.CriticalResultPhone) ?? Clean(prescriber.MobilePhone) ?? Clean(prescriber.Phone);

    /// <summary>
    /// The contact a critical call is recorded with: the number the operator says they dialled,
    /// else, for a telephone call, the one the screen offered (<see cref="CriticalContact"/>). A
    /// value told in person was dialled to nobody.
    /// </summary>
    public static string? ContactUsed(string? dialled, OrderPrescriber? prescriber, NotificationChannel channel) =>
        Clean(dialled) ?? (channel == NotificationChannel.Telephone ? CriticalContact(prescriber) : null);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
