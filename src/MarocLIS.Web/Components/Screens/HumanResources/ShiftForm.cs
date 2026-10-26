using MarocLIS.Domain.HumanResources;

namespace MarocLIS.Web.Components.Screens.HumanResources;

/// <summary>The post typed in the planning's ShiftDialog: its kind, its hours and a note.</summary>
public sealed record ShiftForm(ShiftKind Kind, TimeOnly Start, TimeOnly End, string? Note);
