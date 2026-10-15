namespace MarocLIS.Desktop.Controls;

/// <summary>
/// Value-axis arithmetic for the hand-drawn charts, kept free of WinForms so it can be tested.
/// <para>
/// Ticks fall on 1, 2, 2.5 or 5 times a power of ten. An axis labelled 4.37, 5.12, 5.87 makes the
/// reader do the subtraction a chart is there to spare them; 4.0, 4.5, 5.0 does not.
/// </para>
/// </summary>
public static class ChartScale
{
    /// <summary>
    /// The ticks of an axis covering <paramref name="min"/> to <paramref name="max"/>, in at most
    /// about <paramref name="maxTicks"/> steps, starting at or below the minimum and ending at or
    /// above the maximum. A single value (min equal to max) gets an axis around it rather than a
    /// zero-height one.
    /// </summary>
    public static IReadOnlyList<decimal> NiceTicks(decimal min, decimal max, int maxTicks = 6)
    {
        if (max < min) (min, max) = (max, min);
        maxTicks = Math.Max(2, maxTicks);

        if (max == min)
        {
            // One value: a band of ±10 % around it, or ±1 around zero.
            var half = min == 0m ? 1m : Math.Abs(min) * 0.1m;
            min -= half;
            max += half;
        }

        var step = NiceStep((max - min) / (maxTicks - 1));
        var first = Math.Floor(min / step) * step;
        var last = Math.Ceiling(max / step) * step;

        var ticks = new List<decimal>();

        // Bounded: a pathological step cannot spin forever.
        for (var tick = first; tick <= last + step / 2m && ticks.Count <= maxTicks * 3; tick += step)
            ticks.Add(tick);

        return ticks;
    }

    /// <summary>The nearest of 1, 2, 2.5, 5 or 10 times a power of ten at or above <paramref name="rough"/>.</summary>
    public static decimal NiceStep(decimal rough)
    {
        if (rough <= 0m) return 1m;

        var exponent = (int)Math.Floor(Math.Log10((double)rough));
        var power = Pow10(exponent);
        var fraction = rough / power;

        var nice = fraction <= 1m ? 1m
            : fraction <= 2m ? 2m
            : fraction <= 2.5m ? 2.5m
            : fraction <= 5m ? 5m
            : 10m;

        return nice * power;
    }

    /// <summary>The decimals a tick label needs for <paramref name="step"/> to read exactly.</summary>
    public static int DecimalsFor(decimal step)
    {
        var decimals = 0;
        var scaled = Math.Abs(step);

        while (decimals < 6 && scaled != Math.Floor(scaled))
        {
            scaled *= 10m;
            decimals++;
        }

        return decimals;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;

        // Decimal holds 28 digits; clamped so an absurd range cannot overflow.
        exponent = Math.Clamp(exponent, -20, 20);

        for (var i = 0; i < Math.Abs(exponent); i++)
            result = exponent > 0 ? result * 10m : result / 10m;

        return result;
    }
}
