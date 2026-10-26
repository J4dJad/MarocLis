using System.Globalization;
using System.Text;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// Mindray's reading of HL7 v2.3.1 over MLLP, shared by its BS chemistry, BC haematology and CL
/// immunoassay analysers, as their LIS interface manuals describe it.
/// <list type="bullet">
/// <item>Results come as ORU^R01, one message per sample. MSH-16, which the standard keeps for the
/// application acknowledgement type, says what they are: 0 a sample, 1 a calibration, 2 a quality
/// control.</item>
/// <item>Every message is acknowledged with the same trigger (ACK^R01, ACK^Q03), MSA-6 carrying the
/// error condition (0 when accepted, 207 an application error), followed by ERR.</item>
/// <item>A worklist query is QRY^Q02 with the barcode in QRD-8. The LIS answers QCK^Q02 — QAK-2 « OK »
/// when it has the tube, « NF » when it does not — and, when it has it, DSR^Q03 carrying the patient
/// and the tests as numbered DSP segments, which the analyser acknowledges with ACK^Q03.</item>
/// </list>
/// <para>
/// What a real trace has to confirm is kept out of fixed code where it can be: where the barcode and
/// the test code sit is the link's message format; the lot and level of a control are looked for in
/// the whole of OBR-2 to OBR-6 rather than in one field.
/// </para>
/// </summary>
public static class MindrayHl7
{
    public const string Version = "2.3.1";

    /// <summary>MSH-16 of a sample's results.</summary>
    public const string SampleResult = "0";

    /// <summary>MSH-16 of a calibration's results.</summary>
    public const string CalibrationResult = "1";

    /// <summary>MSH-16 of a quality control's results.</summary>
    public const string QcResult = "2";

    /// <summary>The DSP line of the first test: lines 1 to 28 are the patient and the sample.</summary>
    public const int FirstTestLine = 29;

    /// <summary>What the message carries, from MSH-16: « 0 » (or nothing) a sample.</summary>
    public static string ResultKind(Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Field("MSH", 16)?.Trim() is { Length: > 0 } kind ? kind : SampleResult;
    }

    /// <summary>
    /// What a control's OBR says about the material, as one text the lot matcher can search: the
    /// manuals put the control's number, name, lot, expiry and level in OBR-2 to OBR-6, and a
    /// level sent as a letter (H, M, L) is spelt out so the level matcher reads it.
    /// </summary>
    public static string ControlIdentifier(Hl7Message message, string[] obr)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parts = new List<string>();

        for (var field = 2; field <= 6; field++)
        {
            var value = message.Field(obr, field);
            if (string.IsNullOrWhiteSpace(value)) continue;

            var text = value.Replace(message.Encoding.Component, ' ').Trim();
            var level = text.ToUpperInvariant() switch
            {
                "H" => "HIGH",
                "M" => "NORMAL",
                "L" => "LOW",
                _ => null
            };

            // The level field first: a control named « Control Normal » may well be run at level H,
            // and the level matcher takes the first level word it reads.
            if (level is not null) parts.Insert(0, level);
            else parts.Add(text);
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The acknowledgement of a result message or of a DSR: ACK with the inbound trigger, MSA-6 the
    /// error condition, then ERR. <paramref name="errorText"/> makes it AE (code 207), which keeps
    /// the results on the analyser for a resend.
    /// </summary>
    public static string BuildAck(Hl7Message inbound, string sendingApplication, DateTimeOffset now,
                                  string? errorText = null)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        var trigger = TriggerOf(inbound) ?? "R01";
        var builder = new StringBuilder();

        Header(builder, inbound, sendingApplication, $"ACK^{trigger}", now, inbound.Field("MSH", 16));
        Msa(builder, inbound, errorText);
        return builder.ToString();
    }

    /// <summary>QCK^Q02: whether the LIS has a worklist for the queried tube — QAK-2 « OK » or « NF ».</summary>
    public static string BuildQueryAck(Hl7Message query, string sendingApplication, DateTimeOffset now,
                                       bool found)
    {
        ArgumentNullException.ThrowIfNull(query);

        var f = query.Encoding.Field;
        var builder = new StringBuilder();

        Header(builder, query, sendingApplication, "QCK^Q02", now, null);
        Msa(builder, query, null);
        builder.Append("QAK").Append(f).Append("SR").Append(f).Append(found ? "OK" : "NF").Append(f).Append('\r');
        return builder.ToString();
    }

    /// <summary>
    /// DSR^Q03: the tube's worklist, the query's QRD and QRF echoed, then the patient and the sample
    /// as DSP 1 to 28 and one DSP per test from 29, each test as « code^^^ », the code being the
    /// one the link maps — the code the analyser reports its result under.
    /// </summary>
    public static string BuildWorklist(Hl7Message query, InstrumentOrderDownload order,
                                       string sendingApplication, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(order);

        var f = query.Encoding.Field;
        var c = query.Encoding.Component;
        var builder = new StringBuilder();

        Header(builder, query, sendingApplication, "DSR^Q03", now, null);
        Msa(builder, query, null);
        builder.Append("QAK").Append(f).Append("SR").Append(f).Append("OK").Append(f).Append('\r');

        foreach (var name in (string[])["QRD", "QRF"])
            if (query.FirstSegment(name) is { } echoed)
                builder.Append(string.Join(f, echoed)).Append('\r');

        var lines = new string?[FirstTestLine - 1];
        lines[0] = order.PatientId;                                                  // admission number
        lines[2] = order.PatientName;                                                // name
        lines[3] = order.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) is { } dob
            ? dob + "000000" : null;                                                 // birth date
        lines[4] = order.Sex switch { "M" => "M", "F" => "F", _ => "O" };            // sex
        lines[20] = order.SpecimenId;                                                // sample barcode
        lines[22] = order.CollectedAt is { } at
            ? at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : null;    // sample time
        lines[23] = order.Priority == "S" ? "Y" : "N";                              // STAT
        lines[25] = SampleType(order.SpecimenType);                                  // sample type

        var line = 1;
        foreach (var value in lines)
            Dsp(builder, query, line++, query.Escape(value));

        foreach (var code in order.InstrumentTestCodes)
            Dsp(builder, query, line++, $"{query.Escape(code)}{c}{c}{c}");

        builder.Append("DSC").Append(f).Append(f).Append('\r');
        return builder.ToString();
    }

    /// <summary>The sample type in the words the analyser lists (serum, plasma, urine, CSF, other); empty when unknown.</summary>
    public static string? SampleType(string? specimenType)
    {
        var type = specimenType?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(type)) return null;

        if (type.StartsWith("SER", StringComparison.Ordinal) || type.StartsWith("SÉR", StringComparison.Ordinal)) return "serum";
        if (type.StartsWith("PLA", StringComparison.Ordinal)) return "plasma";
        if (type.StartsWith("UR", StringComparison.Ordinal)) return "urine";
        if (type is "LCR" or "CSF") return "CSF";
        return "other";
    }

    private static string? TriggerOf(Hl7Message message) =>
        message.FirstSegment("MSH") is { } msh ? message.Component(msh, 9, 2) : null;

    private static void Header(StringBuilder builder, Hl7Message inbound, string sendingApplication,
                               string type, DateTimeOffset now, string? resultKind)
    {
        var f = inbound.Encoding.Field;

        builder.Append("MSH").Append(f)
               .Append(inbound.Encoding.EncodingCharacters).Append(f)
               .Append(sendingApplication).Append(f)
               .Append(f)                                                   // sending facility
               .Append(inbound.SendingApplication ?? string.Empty).Append(f)
               .Append(inbound.SendingFacility ?? string.Empty).Append(f)
               .Append(now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(f)
               .Append(f)                                                   // security
               .Append(type).Append(f)
               .Append(inbound.ControlId ?? "1").Append(f)
               .Append('P').Append(f)
               .Append(inbound.Version ?? Version).Append(f)
               .Append(f).Append(f).Append(f)                               // MSH-13 to MSH-15
               .Append(resultKind ?? string.Empty).Append(f)                // MSH-16, echoed on a result's ACK
               .Append(f)
               .Append(inbound.Field("MSH", 18) ?? "ASCII").Append(f).Append(f).Append(f)
               .Append('\r');
    }

    private static void Msa(StringBuilder builder, Hl7Message inbound, string? errorText)
    {
        var f = inbound.Encoding.Field;

        builder.Append("MSA").Append(f)
               .Append(errorText is null ? "AA" : "AE").Append(f)
               .Append(inbound.ControlId ?? string.Empty).Append(f)
               .Append(errorText is null ? "Message accepted" : inbound.Escape(errorText)).Append(f)
               .Append(f).Append(f)
               .Append(errorText is null ? "0" : "207").Append(f)
               .Append('\r');

        builder.Append("ERR").Append(f).Append(errorText is null ? "0" : "207").Append(f).Append('\r');
    }

    private static void Dsp(StringBuilder builder, Hl7Message query, int line, string? value)
    {
        var f = query.Encoding.Field;
        builder.Append("DSP").Append(f)
               .Append(line.ToString(CultureInfo.InvariantCulture)).Append(f).Append(f)
               .Append(value ?? string.Empty).Append(f).Append(f).Append(f)
               .Append('\r');
    }
}
