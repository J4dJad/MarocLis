using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Sequencing;

/// <summary>
/// A gapless counter, scoped by purpose, site and period. Used for accession numbers, invoice
/// numbers, barcodes, non-conformity references and purchase orders.
/// <para>
/// Moroccan tax law requires invoices to be numbered in an unbroken chronological series, so a
/// database IDENTITY is not adequate: identities burn values on rolled-back transactions and leave
/// holes an auditor will ask about. Allocation instead increments this row under a pessimistic
/// lock inside the same transaction as the document, so a rollback returns the number too.
/// </para>
/// </summary>
public sealed class NumberSequence : Entity
{
    private NumberSequence() { }

    public NumberSequence(SequencePurpose purpose, Guid? siteId, int? year, string format)
    {
        Purpose = purpose;
        SiteId = siteId;
        Year = year;
        Format = format;
        NextValue = 1;
    }

    public SequencePurpose Purpose { get; private set; }

    /// <summary>Null for an installation-wide sequence.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>Null for a sequence that never resets.</summary>
    public int? Year { get; private set; }

    /// <summary>
    /// Composite format string. Supported tokens: <c>{SITE}</c>, <c>{YEAR}</c>, <c>{YY}</c>,
    /// <c>{MM}</c>, <c>{SEQ:n}</c> where <c>n</c> is the zero-padded width.
    /// Example: <c>FAC/{SITE}/{YEAR}/{SEQ:6}</c> → <c>FAC/CASA/2026/000318</c>.
    /// </summary>
    public string Format { get; set; } = "{SEQ:6}";

    /// <summary>The value the next allocation will use.</summary>
    public long NextValue { get; private set; }

    /// <summary>Highest value handed out, for reconciliation against the documents that exist.</summary>
    public long LastAllocatedValue { get; private set; }

    public DateTimeOffset? LastAllocatedAt { get; private set; }

    /// <summary>
    /// Freezes the sequence. Used when a financial year is closed: no further document may be
    /// numbered into a period whose accounts have been filed.
    /// </summary>
    public bool IsClosed { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>
    /// Takes the next value and renders it. The caller must hold a transaction; the repository
    /// applies <c>UPDLOCK</c> when loading this row so two workstations cannot take the same number.
    /// </summary>
    public Result<string> Allocate(DateTimeOffset now, string? siteCode)
    {
        if (IsClosed)
            return Result.Failure<string>(Error.Regulatory("Sequence_Closed", Purpose));

        var value = NextValue;
        NextValue = value + 1;
        LastAllocatedValue = value;
        LastAllocatedAt = now;

        return Result.Success(Render(value, now, siteCode));
    }

    /// <summary>Renders a value against the format without consuming a number — used for previews.</summary>
    public string Render(long value, DateTimeOffset now, string? siteCode)
    {
        // Invariant culture throughout. These are accession numbers, invoice numbers and claim
        // references — identifiers, not display text. The invoice series in particular has to be
        // unbroken and comparable for the DGI, and a number whose digits depended on the culture of
        // the workstation that allocated it would not be: the same series would carry two different
        // renderings of the same value, and a range query over it would not return a range.
        var result = Format
            .Replace("{SITE}", siteCode ?? string.Empty, StringComparison.Ordinal)
            .Replace("{YEAR}", (Year ?? now.Year).ToString("D4", CultureInfo.InvariantCulture),
                      StringComparison.Ordinal)
            .Replace("{YY}", ((Year ?? now.Year) % 100).ToString("D2", CultureInfo.InvariantCulture),
                      StringComparison.Ordinal)
            .Replace("{MM}", now.Month.ToString("D2", CultureInfo.InvariantCulture),
                      StringComparison.Ordinal);

        // {SEQ:n} — zero-padded to n digits; {SEQ} defaults to six.
        var index = result.IndexOf("{SEQ", StringComparison.Ordinal);
        while (index >= 0)
        {
            var close = result.IndexOf('}', index);
            if (close < 0) break;

            var token = result[index..(close + 1)];
            var width = 6;
            var colon = token.IndexOf(':');
            if (colon > 0 && int.TryParse(token[(colon + 1)..^1], out var parsed) && parsed is > 0 and <= 18)
                width = parsed;

            result = result.Remove(index, token.Length)
                           .Insert(index, value.ToString(new string('0', width),
                                                          CultureInfo.InvariantCulture));
            index = result.IndexOf("{SEQ", StringComparison.Ordinal);
        }

        return result;
    }

    public void Close(DateTimeOffset at)
    {
        IsClosed = true;
        ClosedAt = at;
    }

    public void Reopen()
    {
        IsClosed = false;
        ClosedAt = null;
    }
}

public enum SequencePurpose
{
    /// <summary>Order / file number (numéro de dossier).</summary>
    Accession = 0,

    /// <summary>Patient file number.</summary>
    MedicalRecord = 1,

    /// <summary>Sample barcode.</summary>
    SampleBarcode = 2,

    /// <summary>Invoice — must stay gapless and chronological.</summary>
    Invoice = 3,

    /// <summary>Credit note (avoir), numbered in its own series.</summary>
    CreditNote = 4,

    /// <summary>Cash receipt.</summary>
    Receipt = 5,

    /// <summary>Quotation.</summary>
    Quotation = 6,

    /// <summary>Non-conformity reference.</summary>
    NonConformity = 7,

    /// <summary>Purchase order.</summary>
    PurchaseOrder = 8,

    /// <summary>Third-party payer claim batch.</summary>
    ClaimBatch = 9,

    /// <summary>Quality document reference.</summary>
    ControlledDocument = 10,

    /// <summary>Transport run between a collection centre and the laboratory.</summary>
    TransportRun = 11,

    /// <summary>Dispatch slip for analyses sent to a partner laboratory.</summary>
    SubcontractDispatch = 12,

    /// <summary>Employee number (matricule).</summary>
    EmployeeNumber = 13,

    /// <summary>Work accident or blood exposure file, the reference given to the insurer.</summary>
    WorkAccident = 14
}
