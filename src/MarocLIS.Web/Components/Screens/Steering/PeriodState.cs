using MarocLIS.Application.Steering;

namespace MarocLIS.Web.Components.Screens.Steering;

/// <summary>
/// The period of a steering screen as its filters hold it: the chip pressed and the two dates. A
/// chip gives its days (<see cref="PeriodReport.Range"/>); a date typed is a period of one's own, and
/// the chip « Période libre » takes over, as at the workstation.
/// </summary>
public sealed class PeriodState
{
    public PeriodPreset Preset { get; private set; } = PeriodPreset.ThisMonth;
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }

    public PeriodState(DateOnly today) => Choose(PeriodPreset.ThisMonth, today);

    /// <summary>Presses a chip; true when it gave days to compute (every chip but « Période libre »).</summary>
    public bool Choose(PeriodPreset preset, DateOnly today)
    {
        Preset = preset;
        if (PeriodReport.Range(preset, today) is not { } range) return false;
        (From, To) = range;
        return true;
    }

    public void TypeFrom(DateOnly from)
    {
        From = from;
        Preset = PeriodPreset.Custom;
    }

    public void TypeTo(DateOnly to)
    {
        To = to;
        Preset = PeriodPreset.Custom;
    }
}
