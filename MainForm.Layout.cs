using static ApolloMicBridge.SkyTheme;

namespace ApolloMicBridge;

internal sealed partial class MainForm
{
    private readonly Label statusHeading = new() { AutoSize = true };
    private readonly SkyButton statusDetails = new() { Visible = false, Size = new Size(94, 24), Anchor = AnchorStyles.Right, Margin = Padding.Empty };
    private string fullStatus = "";
    private readonly Label updateStatus = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly SkyButton updateAction = new();
    private readonly SkyProgress updateProgress = new() { Dock = DockStyle.Fill, Height = 3, Visible = false };

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30, 18, 30, 18),
            ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = Texts.Get("Title"), AutoSize = true,
            Font = new Font("Segoe UI Semibold", 24), Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(new Label { Text = "v" + UpdateClient.CurrentVersion, AutoSize = true,
            ForeColor = Muted, Anchor = AnchorStyles.Right, Margin = Padding.Empty }, 1, 0);
        var subtitle = new Label { Text = Texts.Get("Subtitle"), AutoSize = true,
            Dock = DockStyle.Fill, ForeColor = Muted, Margin = new Padding(0, 2, 0, 12) };
        header.Controls.Add(subtitle, 0, 1);
        header.SetColumnSpan(subtitle, 2);
        root.Controls.Add(header, 0, 0);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0, 0, 0, 18) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        var route = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0,
            Padding = new Padding(20, 16, 20, 16), BackColor = Surface, Margin = new Padding(0, 0, 14, 0) };
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddHeading(route, "AudioRoute");
        AddStackedField(route, "Input", input);
        inputFormat.ForeColor = Muted;
        AddRow(route, inputFormat, 5);
        var channelRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        channelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        channelRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        channel.Margin = Padding.Empty;
        asioRate.Margin = new Padding(8, 0, 0, 0);
        channelRow.Controls.Add(channel, 0, 0);
        channelRow.Controls.Add(asioRate, 1, 0);
        channel.AccessibleName = Texts.Get("Channel");
        asioRate.AccessibleName = Texts.Get("AsioRate");
        AddStackedField(route, "Channel", channelRow);
        AddStackedField(route, "Output", cable);
        outputFormat.ForeColor = Muted;
        AddRow(route, outputFormat, 7);
        AddRow(route, windowsStartup, 16);
        columns.Controls.Add(route, 0, 0);

        var signal = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0,
            Padding = new Padding(20, 16, 20, 16), BackColor = Surface, Margin = Padding.Empty };
        signal.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddHeading(signal, "Signal");
        foreach (var (key, control, readout) in new (string, Control, Label)[] { ("Gain", gain, gainLabel), ("Level", meter, meterLabel) })
        {
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(new Label { Text = Texts.Get(key), AutoSize = true, Margin = Padding.Empty }, 0, 0);
            readout.ForeColor = Muted;
            readout.Margin = Padding.Empty;
            heading.Controls.Add(readout, 1, 0);
            AddRow(signal, heading, 8);
            control.AccessibleName = Texts.Get(key);
            AddRow(signal, control, 8);
        }
        AddRow(signal, new Panel { BackColor = Line, Height = 1, Dock = DockStyle.Fill }, 17);
        exclusiveOutput.Text = Texts.Get("ExclusiveOutput");
        AddRow(signal, exclusiveOutput, 16);
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var (key, control, column) in new (string, Control, int)[] { ("OutputBits", outputBits, 0), ("Reserve", reserve, 1) })
        {
            var field = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 0,
                Margin = column == 0 ? new Padding(0, 0, 10, 0) : Padding.Empty };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddStackedField(field, key, control);
            options.Controls.Add(field, column, 0);
        }
        AddRow(signal, options, 8);
        AddRow(signal, new Label { Text = Texts.Get("ReserveHint"), AutoSize = true, ForeColor = Muted, Dock = DockStyle.Fill }, 7);
        columns.Controls.Add(signal, 1, 0);
        root.Controls.Add(columns, 0, 1);

        var feedback = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface,
            Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        feedback.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        feedback.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusHeading.Font = new Font(Font, FontStyle.Bold);
        statusHeading.Margin = new Padding(0, 0, 0, 5);
        status.Margin = Padding.Empty;
        status.AccessibleName = Texts.Get("AudioStatus");
        var feedbackHeading = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        feedbackHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        feedbackHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        feedbackHeading.Controls.Add(statusHeading, 0, 0);
        statusDetails.Text = Texts.Get("StatusDetails");
        statusDetails.Click += (_, _) => SkyDialog.Show(this, Texts.Get("AudioStatus"), fullStatus);
        feedbackHeading.Controls.Add(statusDetails, 1, 0);
        status.SizeChanged += (_, _) => LayoutStatusText();
        feedback.Controls.Add(feedbackHeading, 0, 0);
        feedback.Controls.Add(status, 0, 1);
        root.Controls.Add(feedback, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
            Padding = new Padding(0, 12, 0, 0), Margin = Padding.Empty };
        start.Width = 210;
        actions.Controls.Add(start);
        var sound = new SkyButton { Text = Texts.Get("WindowsSound"), Width = 166 };
        sound.Click += (_, _) => OpenSoundSettings();
        actions.Controls.Add(sound);
        var help = new SkyButton { Text = Texts.Get("Help"), Width = 124 };
        help.Click += (_, _) => SkyDialog.Show(this, Texts.Get("Help"), Texts.Get("Instructions"));
        actions.Controls.Add(help);
        root.Controls.Add(actions, 0, 3);

        var updates = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        updates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        updates.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        updates.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        updates.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        updates.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
        var divider = new Panel { BackColor = Line, Dock = DockStyle.Fill, Margin = Padding.Empty };
        updates.Controls.Add(divider, 0, 0);
        updates.SetColumnSpan(divider, 2);
        updateStatus.Text = Texts.Get("UpdateAutomatic");
        updateStatus.ForeColor = Muted;
        updateStatus.TextAlign = ContentAlignment.MiddleLeft;
        updateStatus.Margin = new Padding(0, 4, 12, 4);
        updates.Controls.Add(updateStatus, 0, 1);
        updateAction.Text = Texts.Get("CheckUpdates");
        updateAction.Width = 186;
        updateAction.Anchor = AnchorStyles.Right;
        updateAction.Margin = Padding.Empty;
        updateAction.Click += async (_, _) =>
        {
            if (availableUpdate is null)
                await CheckForUpdatesAsync();
            else
                await InstallUpdateAsync();
        };
        updates.Controls.Add(updateAction, 1, 1);
        updates.Controls.Add(updateProgress, 0, 2);
        updates.SetColumnSpan(updateProgress, 2);
        root.Controls.Add(updates, 0, 4);
        return root;
    }

    private void LayoutStatusText()
    {
        if (status.Width <= 0 || status.Height <= 0)
            return;
        var needed = TextRenderer.MeasureText(fullStatus, status.Font, new Size(status.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        var overflow = needed.Height > status.Height;
        statusDetails.Visible = overflow;
        var displayed = overflow ? fullStatus.Split("\r\n\r\n", 2)[0] : fullStatus;
        if (status.Text != displayed)
            status.Text = displayed;
    }

    private static void AddHeading(TableLayoutPanel layout, string key)
    {
        var heading = new Label { Text = Texts.Get(key), AutoSize = true, Font = new Font("Segoe UI Semibold", 12) };
        AddRow(layout, heading);
        heading.Margin = new Padding(0, 0, 0, 8);
    }

    private static void AddStackedField(TableLayoutPanel layout, string key, Control control)
    {
        var text = Texts.Get(key);
        AddRow(layout, new Label { Text = text, AutoSize = true, ForeColor = Muted, Dock = DockStyle.Fill }, 8);
        control.AccessibleName = text;
        AddRow(layout, control, 5);
    }

    private static void AddRow(TableLayoutPanel layout, Control control, int spacing = 0)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, spacing, 0, 0);
        layout.Controls.Add(control, 0, row);
    }
}
