namespace MarocLIS.Domain.Orders;

/// <summary>Where the discount of an order (or of an invoice) came from, as the discounts report shows it.</summary>
public enum DiscountSource
{
    None = 0,

    /// <summary>The convention in force for the order: its rate and its negotiated prices.</summary>
    Convention = 1,

    /// <summary>The rate recorded on the prescriber's file.</summary>
    Prescriber = 2,

    /// <summary>A discount profile chosen at the counter (« Personnel », « Cas social »…).</summary>
    Profile = 3,

    /// <summary>A rate or an amount typed at the counter or when the invoice was issued.</summary>
    Manual = 4
}
