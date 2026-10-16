using MarocLIS.Application.Reports;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Records who took a report, and on whose authority.
/// <para>
/// Handing a report over is a disclosure of health data, so the record has to answer a question
/// somebody may ask months later: who received this, and were they entitled to? The recipient kind
/// is what decides the rest of the form — a patient collecting their own report needs nothing
/// beyond the fact, a third party needs the patient's written consent on file (loi 08-09), and a
/// bearer collecting on someone's behalf needs their own name and CIN, because "a man came for it"
/// is not a record.
/// </para>
/// </summary>
internal sealed class ReportDeliveryDialog : MasterDataEditor
{
    private readonly ReportRegisterRow _row;
    private readonly bool _emailReady, _smsReady;

    /// <summary>The last value this form filled in itself, so it is only replaced, never typed-over text.</summary>
    private string? _autoFilled;

    /// <summary>The recipient name this form filled in itself, replaced in the same way.</summary>
    private string? _autoFilledName;

    private readonly ComboBox _channel, _recipientKind;
    private readonly TextBox _recipientName, _recipientIdentifier, _bearerName, _bearerCin,
                             _consentReference;
    private readonly Label _consentNotice, _channelNotice;

    internal DeliveryChannel Channel => Selected<DeliveryChannel>(_channel);
    internal ReportRecipientKind RecipientKind => Selected<ReportRecipientKind>(_recipientKind);

    internal string? RecipientName => Blank(_recipientName);
    internal string? RecipientIdentifier => Blank(_recipientIdentifier);
    internal string? BearerName => Blank(_bearerName);
    internal string? BearerCin => Blank(_bearerCin);

    /// <summary>
    /// The consent authorising a third-party disclosure. A reference is typed rather than picked
    /// from the patient's consent list because the operator is holding the paper; the service
    /// refuses a third-party delivery without one.
    /// </summary>
    internal Guid? AuthorizingConsentId { get; private set; }

    internal ReportDeliveryDialog(ILocalizer localizer, IUserDialogs dialogs,
                                  ReportRegisterRow row, bool emailReady = false, bool smsReady = false)
        : base(localizer, dialogs, "Report.RecordDelivery",
               $"{row.AccessionNumber} v{row.VersionNumber}", 600, 470)
    {
        _row = row;
        _emailReady = emailReady;
        _smsReady = smsReady;

        var identity = new Label
        {
            Text = $"{row.PatientName}   ·   {row.AccessionNumber}   ·   v{row.VersionNumber}"
                   + $"   ·   {localizer[$"ReportCompleteness.{row.Completeness}"]}",
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 12, 560, 20)
        };

        Controls.Add(identity);

        // The prescriber's stated preference is offered as the default channel: it is what the
        // laboratory agreed to, and re-deciding it at the counter is how a prescriber ends up
        // telephoning to ask where the report went.
        _channel = Choice("Report.Channel", 20, 40, 260, DefaultChannel(row.PrescriberPreference),
                          "DeliveryChannel");

        _recipientKind = Choice("Report.RecipientKind", 300, 40, 280,
                                ReportRecipientKind.Patient, "ReportRecipientKind");

        _channelNotice = Notice(20, 86, 560, 30, "Report.ChannelNotice");

        _recipientName = TextField("Report.Recipient", 20, 120, 260,
                              row.PatientName);
        _autoFilledName = row.PatientName;

        _recipientIdentifier = TextField("Report.RecipientIdentifier", 300, 120, 280,
                                     null, latin: true);

        Notice(20, 166, 560, 30, "Report.RecipientIdentifierNotice");

        _bearerName = TextField("Report.Bearer", 20, 200, 260);
        _bearerCin = TextField("Report.BearerCin", 300, 200, 180, null, latin: true, mono: true);

        // As long as the delivery's columns: a longer entry failed the save of the hand-over.
        _recipientName.MaxLength = 200;
        _recipientIdentifier.MaxLength = 250;
        _bearerName.MaxLength = 200;
        _bearerCin.MaxLength = 20;

        Notice(20, 246, 560, 30, "Report.BearerNotice");

        _consentReference = TextField("Report.AuthorizingConsent", 20, 280, 380, null,
                                 latin: true, mono: true);

        _consentNotice = Notice(20, 326, 560, 44, "Report.ThirdPartyConsentNotice",
                                 UiTheme.Abnormal);

        _recipientKind.SelectedIndexChanged += (_, _) => RefreshForKind();
        _channel.SelectedIndexChanged += (_, _) => RefreshForChannel();

        RefreshForKind();
        RefreshForChannel();

        Buttons(400, 600);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    /// <summary>Maps the prescriber's preference onto the channel that carries it.</summary>
    private static DeliveryChannel DefaultChannel(ReportDeliveryPreference preference) =>
        preference switch
        {
            ReportDeliveryPreference.Email => DeliveryChannel.Email,
            ReportDeliveryPreference.Fax => DeliveryChannel.Fax,
            ReportDeliveryPreference.SmsLink => DeliveryChannel.SmsLink,
            ReportDeliveryPreference.Courier => DeliveryChannel.Courier,
            ReportDeliveryPreference.ElectronicInterface => DeliveryChannel.ElectronicInterface,
            _ => DeliveryChannel.PrintedAtCounter
        };

    private void RefreshForKind()
    {
        var kind = RecipientKind;
        var thirdParty = kind == ReportRecipientKind.AuthorizedThirdParty;

        Enable(_consentReference, thirdParty);

        _consentNotice.Visible = thirdParty;

        // A bearer is only meaningful for a hand-over: an e-mail has no bearer.
        var counter = Channel is DeliveryChannel.PrintedAtCounter or DeliveryChannel.Courier;

        Enable(_bearerName, counter);
        Enable(_bearerCin, counter);

        // The name follows the recipient chosen: the patient's stayed in the box when the
        // prescriber was picked, and the hand-over was recorded under the patient's name.
        if (_recipientName.Text.Length == 0 || _recipientName.Text == _autoFilledName)
        {
            _recipientName.Text = kind switch
            {
                ReportRecipientKind.Patient => _row.PatientName,
                ReportRecipientKind.Prescriber => _row.PrescriberName ?? string.Empty,
                _ => string.Empty
            };
            _autoFilledName = _recipientName.Text;
        }

        FillContact();
    }

    /// <summary>
    /// Puts the address the chosen channel needs in the field: the patient's or the prescriber's
    /// e-mail for an e-mail, the patient's mobile for an SMS. Whatever the operator typed is kept.
    /// </summary>
    private void FillContact()
    {
        var contact = (Channel, RecipientKind) switch
        {
            (DeliveryChannel.Email, ReportRecipientKind.Patient) => _row.PatientEmail,
            (DeliveryChannel.Email, ReportRecipientKind.Prescriber) => _row.PrescriberEmail,
            (DeliveryChannel.SmsLink or DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient) => _row.PatientPhone,
            _ => null
        };

        if (_recipientIdentifier.Text.Length > 0 && _recipientIdentifier.Text != _autoFilled) return;

        _recipientIdentifier.Text = contact ?? string.Empty;
        _autoFilled = _recipientIdentifier.Text;
    }

    private void RefreshForChannel()
    {
        var channel = Channel;

        _channelNotice.Text = Localizer[channel switch
        {
            DeliveryChannel.PrintedAtCounter => "Report.CounterChannelNotice",
            DeliveryChannel.Email => _emailReady ? "Report.EmailChannelLive" : "Report.EmailChannelNotConfigured",
            DeliveryChannel.SmsLink => _smsReady ? "Report.SmsChannelLive" : "Report.SmsChannelNotConfigured",
            DeliveryChannel.Fax => "Report.FaxChannelNotice",
            DeliveryChannel.Courier => "Report.CourierChannelNotice",
            _ => "Report.InterfaceChannelNotice"
        }];

        RefreshForKind();
    }

    private static void Enable(TextBox box, bool enabled)
    {
        box.Enabled = enabled;
        box.BackColor = enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
        if (!enabled) box.Text = string.Empty;
    }

    protected override void Accept()
    {
        var kind = RecipientKind;

        if (string.IsNullOrWhiteSpace(_recipientName.Text))
        {
            Dialogs.Warn(Localizer["Report_RecipientNameRequired"], Text);
            _recipientName.Focus();
            return;
        }

        // An electronic channel with nowhere to send it is a delivery that did not happen.
        if (Channel is DeliveryChannel.Email or DeliveryChannel.SmsLink or DeliveryChannel.Fax
            && string.IsNullOrWhiteSpace(_recipientIdentifier.Text))
        {
            Dialogs.Warn(Localizer["Report_RecipientIdentifierRequired"], Text);
            _recipientIdentifier.Focus();
            return;
        }

        if (kind == ReportRecipientKind.AuthorizedThirdParty)
        {
            if (!Guid.TryParse(_consentReference.Text.Trim(), out var consentId))
            {
                Dialogs.Warn(Localizer["Report_ThirdPartyRequiresConsent"], Text);
                _consentReference.Focus();
                return;
            }

            AuthorizingConsentId = consentId;

            // The bearer is the person physically taking it. Without their name and CIN the record
            // says a third party collected it and nothing about who.
            if (Channel == DeliveryChannel.PrintedAtCounter
                && (string.IsNullOrWhiteSpace(_bearerName.Text)
                    || string.IsNullOrWhiteSpace(_bearerCin.Text)))
            {
                Dialogs.Warn(Localizer["Report_BearerRequired"], Text);
                _bearerName.Focus();
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Issues a rectified version of a report.
/// <para>
/// The reason is mandatory and is printed on the face of the new document. A clinician holding the
/// previous version has two reports and no way to tell which to act on unless the new one says what
/// it changes and why — which is also what ISO 15189 requires of an amended report.
/// </para>
/// <para>
/// The kind is a deliberate choice rather than a default, because the three cases mean different
/// things to the reader: a corrected result, an added result, and a corrected patient identity are
/// not the same event, and the last one may mean the previous report went to the wrong person.
/// </para>
/// </summary>
internal sealed class ReportAmendmentDialog : MasterDataEditor
{
    private readonly ComboBox _kind, _language;
    private readonly TextBox _reasonFr, _reasonAr, _conclusionFr, _conclusionAr;
    private readonly Label _kindNotice;

    internal ReportAmendmentKind Kind => Selected<ReportAmendmentKind>(_kind);

    internal AppLanguage Language => Selected<AppLanguage>(_language);

    internal MultilingualText Reason =>
        new(_reasonFr.Text.Trim(), Blank(_reasonAr));

    internal MultilingualText? Conclusion =>
        Blank(_conclusionFr) is { } text ? new MultilingualText(text, Blank(_conclusionAr)) : null;

    internal ReportAmendmentDialog(ILocalizer localizer, IUserDialogs dialogs,
                                   ReportRegisterRow row)
        : base(localizer, dialogs, "Report.IssueAmendment",
               $"{row.AccessionNumber} v{row.VersionNumber}", 620, 470)
    {
        var identity = new Label
        {
            // The versions first: a long patient name cut off the new version's number.
            Text = $"{row.AccessionNumber}   ·   "
                   + localizer.Format("Report.AmendsVersion", row.VersionNumber, row.VersionNumber + 1)
                   + $"   ·   {row.PatientName}",
            AutoEllipsis = true,
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 12, 580, 20)
        };

        Controls.Add(identity);

        if (row.WasDelivered)
        {
            Notice(20, 34, 580, 30, "Report.AlreadyDeliveredNotice", UiTheme.Critical).Text =
                localizer.Format("Report.AlreadyDeliveredNotice",
                                  row.LastDeliveredAt!.Value.ToLocalTime().ToString("dd/MM/yyyy"));
        }
        else
        {
            Notice(20, 34, 580, 30, "Report.NotYetDeliveredNotice");
        }

        // Only the three real amendment kinds are offered: None is what a first release is, and
        // offering it here would let an operator issue an amendment that does not say it is one.
        _kind = Choice("Report.AmendmentKind", 20, 70, 260, ReportAmendmentKind.ResultCorrection,
                       "ReportAmendmentKind",
                       [ReportAmendmentKind.ResultCorrection, ReportAmendmentKind.Addition,
                        ReportAmendmentKind.CommentRevision, ReportAmendmentKind.IdentityCorrection,
                        ReportAmendmentKind.Retraction]);

        _language = Choice("Order.ReportLanguage", 300, 70, 300, row.Language, "AppLanguage");

        _kindNotice = Notice(20, 116, 580, 44, "Report.AmendmentKindNotice");

        _reasonFr = TextField("Report.AmendmentReasonFr", 20, 164, 280);
        _reasonAr = TextField("Report.AmendmentReasonAr", 320, 164, 280, null, arabic: true);

        Notice(20, 210, 580, 30, "Report.AmendmentReasonNotice", UiTheme.Abnormal);

        _conclusionFr = TextField("Report.ConclusionFr", 20, 244, 280);
        _conclusionAr = TextField("Report.ConclusionAr", 320, 244, 280, null, arabic: true);

        // As long as the report's columns.
        _reasonFr.MaxLength = _reasonAr.MaxLength = 1000;
        _conclusionFr.MaxLength = _conclusionAr.MaxLength = 4000;

        Notice(20, 290, 580, 44, "Report.ConclusionNotice");

        Notice(20, 340, 580, 44, "Report.AmendmentSupersedesNotice");

        _kind.SelectedIndexChanged += (_, _) => RefreshKindNotice();

        RefreshKindNotice();

        Buttons(400, 620);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    private void RefreshKindNotice() =>
        _kindNotice.Text = Localizer[Kind switch
        {
            ReportAmendmentKind.ResultCorrection => "Report.ResultCorrectionNotice",
            ReportAmendmentKind.Addition => "Report.AdditionNotice",
            ReportAmendmentKind.CommentRevision => "Report.CommentRevisionNotice",
            ReportAmendmentKind.Retraction => "Report.RetractionNotice",
            _ => "Report.IdentityCorrectionNotice"
        }];

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_reasonFr.Text))
        {
            Dialogs.Warn(Localizer["Report_AmendmentReasonRequired"], Text);
            _reasonFr.Focus();
            return;
        }

        // A corrected identity means the earlier report may have gone to the wrong person, which is
        // a notifiable data breach under loi 08-09 rather than a clerical fix. Confirmed rather
        // than warned, because the operator has to decide whether it is really that.
        if (Kind is ReportAmendmentKind.IdentityCorrection or ReportAmendmentKind.Retraction
            && !Dialogs.Confirm(Localizer["Report.ConfirmIdentityAmendment"], Text))
            return;

        DialogResult = DialogResult.OK;
        Close();
    }
}
