namespace MarocLIS.Web.Components.Screens.HumanResources;

/// <summary>A personnel file closed: whether it was saved, and whether anything of it changed (a document added counts).</summary>
public sealed record EmployeeFileClosed(bool Saved, bool Changed);
