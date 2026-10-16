using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Chooses which bench a fiche de paillasse is printed for.
/// <para>
/// The department list carries the number of analyses waiting in each, so an empty bench is seen
/// before anything is printed rather than after a blank sheet comes out. The analyser list follows
/// the department: offering the haematology counter while printing for biochemistry would produce a
/// sheet with nothing on it and no explanation why.
/// </para>
/// </summary>
internal sealed class BenchWorksheetDialog : MasterDataEditor
{
    private readonly BenchWorksheetOptions _options;
    private readonly ComboBox _department;
    private readonly ComboBox _instrument;
    private readonly CheckBox _onlyUrgent;

    internal BenchWorksheetRequest Request =>
        new(SelectedId(_department), SelectedId(_instrument), _onlyUrgent.Checked);

    internal BenchWorksheetDialog(ILocalizer localizer, IUserDialogs dialogs,
                                  BenchWorksheetOptions options, Guid? departmentId = null)
        : base(localizer, dialogs, "BenchSheet.Title", null, 460, 290)
    {
        _options = options;

        var language = localizer.CurrentLanguage;

        _department = Lookup("BenchSheet.Department", 20, 16, 420,
            options.Departments.Select(d => (d.Id,
                $"{d.Name.For(language)} ({d.Code}) — {localizer.Format("BenchSheet.PendingCount", d.PendingCount)}")),
            departmentId, allowNone: false);

        // "All departments" first and selected by default: the morning sheet of a small laboratory,
        // where one technician runs every bench, is the common case.
        _department.Items.Insert(0, new LookupChoice(null, localizer["BenchSheet.AllDepartments"]));
        if (departmentId is null) _department.SelectedIndex = 0;

        _instrument = Lookup("BenchSheet.Instrument", 20, 72, 420, [], null, allowNone: false);

        _onlyUrgent = Check("BenchSheet.OnlyUrgent", 20, 130, false, bold: true);

        _department.SelectedIndexChanged += (_, _) => FillInstruments();
        FillInstruments();

        Buttons(226, 460, "Action.Print");
        Frame();

        RtlLayout.Apply(this, language);
    }

    /// <summary>
    /// The direction-A frame round the fields the editor base laid out: the title in 17 px with the
    /// notice as its sentence, the fields under it, and Cancel then Print at the far end of the foot.
    /// </summary>
    private void Frame()
    {
        var head = DialogFrame.Head(Localizer["BenchSheet.Title"], Localizer["BenchSheet.Notice"], 460);
        var print = (Button)AcceptButton!;
        var cancel = (Button)CancelButton!;
        Controls.Remove(print);
        Controls.Remove(cancel);

        foreach (Control field in Controls)
        {
            field.Top += head.Height - 4;
            if (field is Label caption) { caption.Font = UiTheme.SmallBold; caption.ForeColor = UiTheme.TextSecondary; }
        }

        ClientSize = new Size(460, head.Height + 162 + 58);
        Controls.Add(DialogFrame.Foot(cancel, print));
        Controls.Add(head);
    }

    /// <summary>
    /// The analysers of the chosen department, plus those not attached to any — a shared analyser
    /// with no department belongs to every bench.
    /// </summary>
    private void FillInstruments()
    {
        var departmentId = SelectedId(_department);
        var language = Localizer.CurrentLanguage;

        _instrument.BeginUpdate();
        _instrument.Items.Clear();
        _instrument.Items.Add(new LookupChoice(null, Localizer["BenchSheet.AllInstruments"]));

        foreach (var instrument in _options.Instruments.Where(i => departmentId is null
                                                                   || i.DepartmentId is null
                                                                   || i.DepartmentId == departmentId))
        {
            _instrument.Items.Add(new LookupChoice(instrument.Id,
                                                   $"{instrument.Name.For(language)} ({instrument.Code})"));
        }

        _instrument.SelectedIndex = 0;
        _instrument.EndUpdate();
    }

    protected override void Accept()
    {
        DialogResult = DialogResult.OK;
        Close();
    }
}
