using System.ComponentModel;
using MarocLIS.Application.Orders;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The strip of the scanned prescription, the same on « Nouvelle demande » and on « Dossier »:
/// « Numériser l'ordonnance », « Joindre un fichier », then whether a prescription is attached and
/// how many pages it has, with « Voir » and « Retirer ».
/// <para>
/// The strip only says what was clicked; the screen does the work through
/// <see cref="PrescriptionScanService"/>. The buttons a user may not use are disabled, as the other
/// buttons of the screens are (<see cref="PermissionGate"/>), so the strip keeps its shape.
/// </para>
/// </summary>
public sealed class PrescriptionAttachmentBar : FlowLayoutPanel
{
    private readonly Label _caption = new() { AutoSize = true, Visible = false };
    private readonly Button _scan = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _attach = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _state = new() { AutoSize = true };
    private readonly LinkLabel _view = new() { AutoSize = true };
    private readonly LinkLabel _remove = new() { AutoSize = true };

    private ILocalizer? _localizer;
    private bool _attached;
    private int? _pages;
    private bool _canChange = true;
    private bool _canView = true;

    public event EventHandler? ScanRequested;
    public event EventHandler? AttachRequested;
    public event EventHandler? ViewRequested;
    public event EventHandler? RemoveRequested;

    public PrescriptionAttachmentBar()
    {
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.SurfaceRaised;
        Tag = UiTags.OwnLayout;

        foreach (var button in new[] { _scan, _attach })
        {
            button.Height = 30;
            button.Margin = new Padding(0, 0, 8, 0);
            button.Tag = UiTags.NoMnemonic;
        }

        ButtonIcons.Set(_scan, Glyph.ScanLine, always: true);
        ButtonIcons.Set(_attach, Glyph.Paperclip, always: true);

        _caption.Font = UiTheme.SmallBold;
        _caption.ForeColor = UiTheme.TextSecondary;
        _caption.BackColor = UiTheme.SurfaceRaised;
        _caption.Margin = new Padding(0, 8, 12, 0);

        _state.Font = UiTheme.Body;
        _state.Margin = new Padding(8, 7, 8, 0);
        _state.BackColor = UiTheme.SurfaceRaised;

        foreach (var link in new[] { _view, _remove })
        {
            link.Font = UiTheme.Body;
            link.LinkColor = UiTheme.Accent;
            link.ActiveLinkColor = UiTheme.AccentPressed;
            link.BackColor = UiTheme.SurfaceRaised;
            link.Margin = new Padding(4, 7, 4, 0);
        }

        _remove.LinkColor = UiTheme.CriticalInk;

        _scan.Click += (_, _) => ScanRequested?.Invoke(this, EventArgs.Empty);
        _attach.Click += (_, _) => AttachRequested?.Invoke(this, EventArgs.Empty);
        _view.LinkClicked += (_, _) => ViewRequested?.Invoke(this, EventArgs.Empty);
        _remove.LinkClicked += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);

        Controls.AddRange([_caption, _scan, _attach, _state, _view, _remove]);
        Refresh(null);
    }

    /// <summary>A caption at the head of the strip, where the screen has no caption above it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Caption
    {
        get => _caption.Text;
        set
        {
            _caption.Text = value ?? string.Empty;
            _caption.Visible = value is { Length: > 0 };
        }
    }

    /// <summary>The width the strip needs on one line.</summary>
    public int PreferredWidth
    {
        get
        {
            // Visible reads false until the screen is shown: what the strip means to show is counted.
            var shown = new List<Control> { _scan, _attach, _state };
            if (_caption.Text.Length > 0) shown.Add(_caption);
            if (_attached) shown.AddRange([_view, _remove]);
            return shown.Sum(c => (c is Button ? c.Width : c.PreferredSize.Width) + c.Margin.Horizontal) + 4;
        }
    }

    public void ApplyTexts(ILocalizer localizer)
    {
        _localizer = localizer;
        _scan.Text = localizer["Order.ScanPrescription"];
        _attach.Text = localizer["Order.AttachPrescriptionFile"];
        _view.Text = localizer["Order.ViewPrescription"];
        _remove.Text = localizer["Order.RemovePrescription"];

        foreach (var button in new[] { _scan, _attach })
            button.Width = TextRenderer.MeasureText(button.Text, button.Font).Width + 36 + 28;

        Refresh(localizer);
    }

    /// <summary>Whether a prescription is attached, and its pages when they could be counted.</summary>
    public void ShowAttachment(bool attached, int? pages)
    {
        _attached = attached;
        _pages = pages;
        Refresh(_localizer);
    }

    /// <summary>What the user may do: change (scan, attach, remove) and view.</summary>
    public void Allow(bool change, bool view)
    {
        _canChange = change;
        _canView = view;
        Refresh(_localizer);
    }

    /// <summary>Greys the whole strip while a page is being assembled or saved.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        set
        {
            _scan.Enabled = !value && _canChange;
            _attach.Enabled = !value && _canChange;
            _view.Enabled = !value && _canView && _attached;
            _remove.Enabled = !value && _canChange && _attached;
        }
    }

    private void Refresh(ILocalizer? localizer)
    {
        _scan.Enabled = _canChange;
        _attach.Enabled = _canChange;
        _view.Visible = _attached;
        _remove.Visible = _attached;
        _view.Enabled = _canView;
        _remove.Enabled = _canChange;

        _state.ForeColor = _attached ? UiTheme.Normal : UiTheme.TextSecondary;

        if (localizer is null) return;

        var text = localizer[_attached ? "Order.PrescriptionAttached" : "Order.PrescriptionNone"];
        if (_attached && PrescriptionScanRules.Pages(_pages) is { } pages)
            text += " · " + localizer.Format(pages.Key, pages.Count);

        _state.Text = (_attached ? "✓ " : string.Empty) + text;
    }
}
