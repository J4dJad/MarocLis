namespace MarocLIS.Domain.Identity;

/// <summary>
/// The authoritative catalogue of permissions. Permissions are plain strings so a new module can
/// declare its own without changing this assembly, but everything shipped in the box is listed
/// here to keep the role editor discoverable and to let a startup check detect orphaned grants.
/// </summary>
public static class Permissions
{
    public const string Separator = ".";

    public static class Patients
    {
        public const string View = "patients.view";
        public const string Create = "patients.create";
        public const string Edit = "patients.edit";
        public const string Merge = "patients.merge";
        /// <summary>Reading a patient file is itself logged (loi 08-09); exporting requires a stronger grant.</summary>
        public const string Export = "patients.export";
        public const string Anonymize = "patients.anonymize";
    }

    public static class Orders
    {
        public const string View = "orders.view";
        public const string Create = "orders.create";
        public const string Edit = "orders.edit";
        public const string Cancel = "orders.cancel";
        public const string AddUrgent = "orders.urgent";
    }

    public static class Samples
    {
        public const string View = "samples.view";
        public const string Collect = "samples.collect";
        public const string Receive = "samples.receive";
        public const string Reject = "samples.reject";
        public const string Reprint = "samples.reprint_label";
        public const string Dispatch = "samples.dispatch";
    }

    public static class Results
    {
        public const string View = "results.view";
        public const string Enter = "results.enter";
        public const string Modify = "results.modify";
        /// <summary>Technical validation — plausibility, QC, delta check. Performed by a technician.</summary>
        public const string TechnicalValidate = "results.validate_technical";
        /// <summary>Biological validation — the legally binding release. Biologists only (loi 12-01).</summary>
        public const string BiologicalValidate = "results.validate_biological";
        public const string Unvalidate = "results.unvalidate";
        public const string EnterCritical = "results.enter_critical";
    }

    public static class Reports
    {
        public const string View = "reports.view";
        public const string Print = "reports.print";
        public const string Send = "reports.send";
        public const string Amend = "reports.amend";
        public const string ReprintValidated = "reports.reprint_validated";
    }

    public static class Billing
    {
        public const string View = "billing.view";
        public const string CreateInvoice = "billing.invoice.create";
        public const string CreditNote = "billing.credit_note";
        public const string RecordPayment = "billing.payment.record";
        public const string ApplyDiscount = "billing.discount";
        public const string ManageTariffs = "billing.tariffs.manage";
        public const string CloseCashSession = "billing.cash.close";
        public const string ExportAccounting = "billing.export";
    }

    public static class Quality
    {
        public const string View = "quality.view";
        public const string EnterQc = "quality.qc.enter";
        public const string ReviewQc = "quality.qc.review";
        public const string ManageQcRules = "quality.qc.rules";
        public const string ManageNonConformity = "quality.nc.manage";
        public const string ApproveDocument = "quality.document.approve";
        /// <summary>Drafting, submitting and reviewing controlled documents; approval stays separate.</summary>
        public const string ManageDocuments = "quality.document.manage";
    }

    public static class Inventory
    {
        public const string View = "inventory.view";
        public const string Receive = "inventory.receive";
        public const string Issue = "inventory.issue";
        public const string Adjust = "inventory.adjust";
        public const string ManagePurchaseOrders = "inventory.po.manage";

        /// <summary>Approves a purchase order: committing the laboratory's money is not the buyer's call.</summary>
        public const string ApprovePurchaseOrders = "inventory.po.approve";
    }

    public static class Equipment
    {
        public const string View = "equipment.view";
        public const string Manage = "equipment.manage";
        public const string RecordMaintenance = "equipment.maintenance";
        public const string Calibrate = "equipment.calibrate";
    }

    public static class Instruments
    {
        public const string View = "instruments.view";
        public const string Configure = "instruments.configure";
        public const string StartStop = "instruments.control";
        public const string ReplayMessages = "instruments.replay";
    }

    public static class Catalog
    {
        public const string View = "catalog.view";
        public const string Manage = "catalog.manage";
        public const string ManageReferenceRanges = "catalog.ranges.manage";
    }

    public static class Administration
    {
        public const string ViewUsers = "admin.users.view";
        public const string ManageUsers = "admin.users.manage";
        public const string ManageRoles = "admin.roles.manage";
        public const string ViewAuditTrail = "admin.audit.view";
        public const string ManageSites = "admin.sites.manage";
        public const string ManageSettings = "admin.settings.manage";
        public const string RunMaintenance = "admin.maintenance.run";
    }

    public static class Statistics
    {
        public const string View = "statistics.view";
        public const string EpidemiologicalDeclaration = "statistics.declaration";
    }

    private static readonly Lazy<IReadOnlyList<string>> AllLazy = new(Discover);

    public static IReadOnlyList<string> All => AllLazy.Value;

    public static bool Exists(string permission) => All.Contains(permission, StringComparer.Ordinal);

    /// <summary>Groups permissions by their module prefix, which is how the role editor renders them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByModule() =>
        All.GroupBy(p => p.Split(Separator, 2)[0], StringComparer.Ordinal)
           .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Order(StringComparer.Ordinal)],
                         StringComparer.Ordinal);

    private static IReadOnlyList<string> Discover() =>
    [
        .. typeof(Permissions)
            .GetNestedTypes()
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public |
                                         System.Reflection.BindingFlags.Static |
                                         System.Reflection.BindingFlags.FlattenHierarchy))
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
    ];
}

/// <summary>
/// Roles shipped by default. They map onto the real job functions in a Moroccan laboratory and are
/// seeded on first run; an administrator can create more.
/// </summary>
public static class SystemRoles
{
    /// <summary>Full access, including audit trail and user management.</summary>
    public const string Administrator = "ADMIN";

    /// <summary>Directeur / biologiste responsable — the only role that can release results.</summary>
    public const string ResponsibleBiologist = "BIOLOGISTE_RESPONSABLE";

    /// <summary>Biologiste médical — biological validation within an assigned scope.</summary>
    public const string Biologist = "BIOLOGISTE";

    /// <summary>Technicien de laboratoire — result entry and technical validation.</summary>
    public const string Technician = "TECHNICIEN";

    /// <summary>Infirmier / préleveur — sample collection.</summary>
    public const string Phlebotomist = "PRELEVEUR";

    /// <summary>Secrétariat / accueil — registration, orders, cash.</summary>
    public const string Receptionist = "SECRETARIAT";

    /// <summary>Responsable qualité — QC review, non-conformities, document control.</summary>
    public const string QualityManager = "RESPONSABLE_QUALITE";

    /// <summary>Comptabilité / facturation.</summary>
    public const string Accountant = "COMPTABILITE";

    /// <summary>Médecin prescripteur consulting his own patients' reports, read-only.</summary>
    public const string Prescriber = "PRESCRIPTEUR";
}
