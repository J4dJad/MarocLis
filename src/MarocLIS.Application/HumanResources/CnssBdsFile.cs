using System.Globalization;
using System.Text;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.HumanResources;

/// <summary>
/// The situation of an insured person on the BDS line, when not a normal month.
/// </summary>
public enum BdsSituation
{
    Normal = 0,
    /// <summary>SO: left in or before the month, or not paid this month.</summary>
    Left = 1,
    /// <summary>DE.</summary>
    Deceased = 2,
    /// <summary>IT: in the spec's list, never produced here.</summary>
    It = 3,
    /// <summary>IL: in the spec's list, never produced here.</summary>
    Il = 4,
    /// <summary>AT: the whole month on a work-accident stoppage.</summary>
    WorkAccident = 5,
    /// <summary>CS: the whole month on unpaid leave.</summary>
    UnpaidLeave = 6,
    /// <summary>MS: never produced here, sick leave is paid by the laboratory and its days declared.</summary>
    Sickness = 7,
    /// <summary>MP: never produced here.</summary>
    OccupationalDisease = 8
}

/// <summary>The month's préétabli as DAMANCOM issues it (AFFEBDS_&lt;affilié&gt;_&lt;AAAAMM&gt;.txt).</summary>
/// <param name="HeaderA01">The A01 record as read, padded to 260: copied whole into B01.</param>
public sealed record BdsPreestablished(string TransferId, string AffiliateNumber, string Period, string HeaderA01,
                                       IReadOnlyList<BdsInsured> Insured)
{
    public int Year => int.Parse(Period[..4], CultureInfo.InvariantCulture);
    public int Month => int.Parse(Period[4..], CultureInfo.InvariantCulture);
}

/// <summary>One A02 line: an insured person the CNSS expects on the declaration, with the family allowances.</summary>
public sealed record BdsInsured(long InsuredNumber, string Name, int Children, long AllowanceToPay, long AllowanceToDeduct,
                                long AllowanceNet);

/// <summary>
/// What the payroll says about one insured person this month. Amounts in dirhams; the file has
/// them in centimes.
/// </summary>
public sealed record BdsSalary(long InsuredNumber, string FullName, string? Cin, int Days, decimal RealSalary,
                               decimal CappedSalary, BdsSituation Situation = BdsSituation.Normal);

/// <summary>
/// The CNSS e-BDS télédéclaration file, spec "e-BDS v2 (février 2006)": fixed records of 260
/// ASCII characters ending with CRLF, numbers right-aligned with zeros, text left-aligned with
/// spaces, amounts in centimes.
/// <para>
/// DAMANCOM issues the month's préétabli (A00 transfer, A01 employer, A02 one per insured person
/// it expects, A03 totals). The employer returns the BDS built from it (DS_&lt;affilié&gt;_&lt;AAAAMM&gt;.txt):
/// B00 and B01 copied, B02 one line per A02 with days and salary, B03 their totals, B04 the
/// entrants (paid but not in the préétabli), B05 their totals and B06 the totals of both.
/// </para>
/// </summary>
public static class CnssBdsFile
{
    public const int RecordLength = 260;

    /// <summary>
    /// The L_Situation codes and the value each adds to the B02 control sum. This follows the e-BDS
    /// v2 spec as understood here; DAMANCOM checks the file when it is uploaded and says which
    /// line it refuses.
    /// </summary>
    public static readonly IReadOnlyDictionary<BdsSituation, (string Code, int Value)> Situations =
        new Dictionary<BdsSituation, (string, int)>
        {
            [BdsSituation.Normal] = ("  ", 0),
            [BdsSituation.Left] = ("SO", 1),
            [BdsSituation.Deceased] = ("DE", 2),
            [BdsSituation.It] = ("IT", 3),
            [BdsSituation.Il] = ("IL", 4),
            [BdsSituation.WorkAccident] = ("AT", 5),
            [BdsSituation.UnpaidLeave] = ("CS", 6),
            [BdsSituation.Sickness] = ("MS", 7),
            [BdsSituation.OccupationalDisease] = ("MP", 8)
        };

    /// <summary>Days declared at most for a month.</summary>
    public const int MaximumDays = 26;

    /// <summary>The largest insured number the 9-digit N_Num_Assure holds.</summary>
    public const long MaximumInsuredNumber = 999_999_999L;

    public static string FileName(string affiliate, string period) => $"DS_{affiliate}_{period}.txt";

    // ---- Reading the préétabli --------------------------------------------------------------------

    /// <summary>Reads the préétabli. Fails with the number of the first line it cannot read.</summary>
    public static Result<BdsPreestablished> Parse(byte[] content)
    {
        var text = Encoding.Latin1.GetString(content);
        string? transfer = null, header = null;
        var insured = new List<BdsInsured>();

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.Length > RecordLength) return Invalid(i + 1);
            line = line.PadRight(RecordLength);

            switch (line[..3])
            {
                case "A00":
                    transfer = line.Substring(3, 14);
                    break;
                case "A01":
                    if (!IsNumber(line.Substring(3, 7)) || !IsPeriod(line.Substring(10, 6))) return Invalid(i + 1);
                    header = line;
                    break;
                case "A02":
                    if (header is null) return Invalid(i + 1);
                    long[] numbers = [0, 0, 0, 0, 0];
                    (int Start, int Length)[] fields = [(16, 9), (85, 2), (87, 6), (93, 6), (99, 6)];
                    for (var f = 0; f < fields.Length; f++)
                        if (!TryNumber(line.Substring(fields[f].Start, fields[f].Length), out numbers[f])) return Invalid(i + 1);
                    if (numbers[0] == 0) return Invalid(i + 1);

                    insured.Add(new BdsInsured(numbers[0], line.Substring(25, 60).TrimEnd(), (int)numbers[1], numbers[2],
                                               numbers[3], numbers[4]));
                    break;
                case "A03":
                    break;
                default:
                    return Invalid(i + 1);
            }
        }

        if (transfer is null || header is null)
            return Result.Failure<BdsPreestablished>(Error.Validation("Payroll_BdsFileNoHeader"));

        return Result.Success(new BdsPreestablished(transfer, header.Substring(3, 7), header.Substring(10, 6), header,
                                                    insured));
    }

    private static Result<BdsPreestablished> Invalid(int line) =>
        Result.Failure<BdsPreestablished>(Error.Validation("Payroll_BdsFileInvalid", line));

    private static bool IsNumber(string field) => field.Trim().Length > 0 && field.Trim().All(char.IsAsciiDigit);

    private static bool IsPeriod(string field) =>
        IsNumber(field) && field.Length == 6 && int.Parse(field[4..], CultureInfo.InvariantCulture) is >= 1 and <= 12;

    private static bool TryNumber(string field, out long value)
    {
        var trimmed = field.Trim();
        value = 0;
        return trimmed.Length == 0 || (trimmed.All(char.IsAsciiDigit) && long.TryParse(trimmed, out value));
    }

    /// <summary>
    /// The préétabli must be the employer's, for the month declared. Affiliate numbers are compared
    /// as numbers: "0123456" and "123456" are the same affiliate.
    /// </summary>
    public static Result Matches(BdsPreestablished file, string? employerAffiliate, int year, int month)
    {
        var employer = Digits(employerAffiliate);
        if (employer.Length == 0) return Result.Failure(Error.Validation("Payroll_BdsNoAffiliation"));
        if (employer.TrimStart('0') != file.AffiliateNumber.Trim().TrimStart('0'))
            return Result.Failure(Error.Validation("Payroll_BdsWrongAffiliate", file.AffiliateNumber.Trim(), employerAffiliate!.Trim()));
        if (file.Year != year || file.Month != month)
            return Result.Failure(Error.Validation("Payroll_BdsWrongPeriod", $"{file.Month:00}/{file.Year}", $"{month:00}/{year}"));
        return Result.Success();
    }

    /// <summary>The digits of a CNSS number as typed ("123 456 789", "123-456-789").</summary>
    public static string Digits(string? value) =>
        new([.. (value ?? string.Empty).Where(char.IsAsciiDigit)]);

    // ---- Writing the BDS --------------------------------------------------------------------------

    /// <summary>
    /// The BDS for <paramref name="file"/>. Each A02 insured person is declared with their line of
    /// <paramref name="salaries"/>; one without is declared with no day and no salary, in the
    /// situation <paramref name="absent"/> gives (SO when it says nothing). The salaries of
    /// persons the préétabli does not list are the entrants.
    /// </summary>
    public static byte[] Build(BdsPreestablished file, IReadOnlyList<BdsSalary> salaries,
                               IReadOnlyDictionary<long, BdsSituation>? absent = null)
    {
        var affiliate = Num(long.Parse(Digits(file.AffiliateNumber), CultureInfo.InvariantCulture), 7);
        var period = file.Period;
        var byNumber = salaries.GroupBy(s => s.InsuredNumber).ToDictionary(g => g.Key, g => g.First());
        var expected = file.Insured.Select(i => i.InsuredNumber).ToHashSet();

        var records = new List<string>
        {
            "B00" + Alpha(file.TransferId, 14) + "B0" + new string(' ', 241),
            // As read, apart from the type: DAMANCOM checks the employer's details against its own.
            "B01" + Alpha(file.HeaderA01[3..], RecordLength - 3)
        };

        // B02: the préétabli's insured persons.
        long count = 0, children = 0, toPay = 0, toDeduct = 0, net = 0, numbers = 0, days = 0, real = 0, capped = 0, control = 0;
        foreach (var insured in file.Insured.OrderBy(i => i.InsuredNumber))
        {
            var salary = byNumber.GetValueOrDefault(insured.InsuredNumber);
            var situation = salary?.Situation
                            ?? absent?.GetValueOrDefault(insured.InsuredNumber, BdsSituation.Left)
                            ?? BdsSituation.Left;
            var (code, value) = Situations[situation];
            var (d, r, c) = Amounts(salary);
            // N_AF_A_Reverser: no family allowance is returned to the CNSS here, always 0.
            const long returned = 0;
            var check = insured.InsuredNumber + returned + d + r + c + value;

            records.Add("B02" + affiliate + period + Num(insured.InsuredNumber, 9) + Alpha(insured.Name, 60)
                        + Num(insured.Children, 2) + Num(insured.AllowanceToPay, 6) + Num(insured.AllowanceToDeduct, 6)
                        + Num(insured.AllowanceNet, 6) + Num(returned, 6) + Num(d, 2) + Num(r, 13) + Num(c, 9) + code
                        + Num(check, 19) + new string(' ', 104));

            count++; children += insured.Children; toPay += insured.AllowanceToPay; toDeduct += insured.AllowanceToDeduct;
            net += insured.AllowanceNet; numbers += insured.InsuredNumber; days += d; real += r; capped += c; control += check;
        }

        records.Add("B03" + affiliate + period + Num(count, 6) + Num(children, 6) + Num(toPay, 12) + Num(toDeduct, 12)
                    + Num(net, 12) + Num(numbers, 15) + Num(0, 12) + Num(days, 6) + Num(real, 15) + Num(capped, 13)
                    + Num(control, 19) + new string(' ', 116));

        // B04: paid this month but not expected by the CNSS.
        long eCount = 0, eNumbers = 0, eDays = 0, eReal = 0, eCapped = 0, eControl = 0;
        foreach (var entrant in byNumber.Values.Where(s => !expected.Contains(s.InsuredNumber)).OrderBy(s => s.InsuredNumber))
        {
            var (d, r, c) = Amounts(entrant);
            var check = entrant.InsuredNumber + d + r + c;

            records.Add("B04" + affiliate + period + Num(entrant.InsuredNumber, 9) + Alpha(entrant.FullName, 60)
                        + Alpha(entrant.Cin, 8) + Num(d, 2) + Num(r, 13) + Num(c, 9) + Num(check, 19) + new string(' ', 124));

            eCount++; eNumbers += entrant.InsuredNumber; eDays += d; eReal += r; eCapped += c; eControl += check;
        }

        records.Add(Recap("B05", affiliate, period, eCount, eNumbers, eDays, eReal, eCapped, eControl));
        records.Add(Recap("B06", affiliate, period, count + eCount, numbers + eNumbers, days + eDays, real + eReal,
                          capped + eCapped, control + eControl));

        var text = new StringBuilder();
        foreach (var record in records)
        {
            if (record.Length != RecordLength)
                throw new InvalidOperationException($"BDS record {record[..3]} is {record.Length} characters long.");
            text.Append(record).Append("\r\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static string Recap(string type, string affiliate, string period, long count, long numbers, long days,
                                long real, long capped, long control) =>
        type + affiliate + period + Num(count, 6) + Num(numbers, 15) + Num(days, 6) + Num(real, 15) + Num(capped, 13)
        + Num(control, 19) + new string(' ', 170);

    /// <summary>Days (26 at most), real and capped salary in centimes.</summary>
    private static (long Days, long Real, long Capped) Amounts(BdsSalary? salary) =>
        salary is null
            ? (0, 0, 0)
            : (Math.Clamp(salary.Days, 0, MaximumDays), Centimes(salary.RealSalary), Centimes(Math.Min(salary.CappedSalary, salary.RealSalary)));

    private static long Centimes(decimal dirhams) =>
        (long)Math.Round(Math.Max(0m, dirhams) * 100m, MidpointRounding.AwayFromZero);

    /// <summary>A numeric field: right-aligned, zero-padded.</summary>
    private static string Num(long value, int width)
    {
        var text = Math.Max(0L, value).ToString(CultureInfo.InvariantCulture);
        if (text.Length > width)
            throw new InvalidOperationException($"{value} does not fit a {width}-digit BDS field.");
        return text.PadLeft(width, '0');
    }

    /// <summary>A text field: ASCII, upper case, without accents, left-aligned and space-padded.</summary>
    internal static string Alpha(string? value, int width)
    {
        var text = Ascii(value);
        return text.Length > width ? text[..width] : text.PadRight(width);
    }

    /// <summary>Upper case, accents taken off, anything else outside printable ASCII made a space.</summary>
    public static string Ascii(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var text = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(c is >= ' ' and <= '~' ? char.ToUpperInvariant(c) : ' ');
        }
        return text.ToString();
    }
}
