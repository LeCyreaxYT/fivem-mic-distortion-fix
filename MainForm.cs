using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using static ApolloMicBridge.SkyTheme;

namespace ApolloMicBridge;

internal sealed partial class MainForm : SkyWindow
{
    private static readonly Size DefaultClientSize = new(1040, 800);
    private readonly SkySelect input = new() { Dock = DockStyle.Fill };
    private readonly SkySelect channel = new() { Dock = DockStyle.Fill };
    private readonly SkySelect cable = new() { Dock = DockStyle.Fill };
    private readonly SkySelect asioRate = new() { Width = 105 };
    private readonly SkySelect reserve = new() { Dock = DockStyle.Fill };
    private readonly SkySelect outputBits = new() { Dock = DockStyle.Fill, Enabled = false };
    private readonly SkySlider gain = new() { Minimum = -24, Maximum = 12, Value = 0, Dock = DockStyle.Fill };
    private readonly AudioLevelMeter meter = new() { Dock = DockStyle.Fill, Height = 12 };
    private readonly Label inputFormat = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label outputFormat = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label gainLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label meterLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.None, BackColor = Surface, BorderStyle = BorderStyle.None, TabStop = true };
    private readonly SkyButton start = new() { Primary = true };
    private readonly SkyToggle windowsStartup = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly SkyToggle exclusiveOutput = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application };
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem trayStart = new();
    private readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer deviceTimer = new() { Interval = 400 };
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly DeviceNotifications notifications;
    private AudioBridge? bridge;
    private UserSettings settings = new(InputBackend.Wasapi, null, null);
    private DateTime startedAt;
    private DateTime lastCaptureAt;
    private long previousCaptured;
    private long previousMissing;
    private long previousDropped;
    private bool updating;
    private volatile bool closing;
    private bool inputReady;
    private bool initialized;
    private bool firstBackgroundOpen;
    private bool pendingAutoStart;
    private bool exitRequested;
    private bool cleanupFailed;
    private bool settingsSaveFailed;
    private string runningMessage = "";
    internal int DeviceRefreshCount { get; private set; }
    private int deviceUpdateRequests;
    internal int DeviceUpdateRequests => Volatile.Read(ref deviceUpdateRequests);

    public MainForm(bool autoStart = false, bool background = false, bool checkUpdates = true)
    {
        SuspendLayout();
        Text = Texts.Get("Title");
        Font = new Font("Segoe UI", 10);
        BackColor = Background;
        ForeColor = Ink;
        DoubleBuffered = true;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = DefaultClientSize;
        MinimumSize = new Size(940, 760);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));
        StartPosition = FormStartPosition.CenterScreen;
        Content.Controls.Add(BuildLayout());
        windowsStartup.Text = Texts.Get("WindowsStartup");
        windowsStartup.CheckedChanged += (_, _) =>
        {
            if (updating)
                return;
            try { StartupRegistration.SetEnabled(windowsStartup.Checked); }
            catch (Exception error)
            {
                AppLog.Write("Cannot change Windows startup registration", error);
                updating = true;
                windowsStartup.Checked = !windowsStartup.Checked;
                updating = false;
                SetStatus(Texts.Get("StartupError") + "\n" + error.Message, true);
            }
        };
        trayMenu.Items.Add(Texts.Get("OpenWindow"), null, (_, _) => OpenWindow());
        trayStart.Click += (_, _) => ToggleBridge();
        trayMenu.Items.Add(trayStart);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(Texts.Get("Quit"), null, (_, _) => RequestExit());
        tray.ContextMenuStrip = trayMenu;
        trayMenu.Renderer = new ToolStripProfessionalRenderer(new SkyMenuColors());
        trayMenu.ForeColor = Ink;
        tray.DoubleClick += (_, _) => OpenWindow();
        tray.Text = Texts.Get("TrayStopped");
        tray.Visible = true;
        asioRate.Items.AddRange([44100, 48000, 96000]);
        asioRate.SelectedItem = 48000;
        reserve.Items.AddRange([5, 10, 20]);
        reserve.SelectedItem = 5;
        outputBits.Items.AddRange([16, 24]);
        outputBits.SelectedItem = 16;
        notifications = new DeviceNotifications(ScheduleDeviceUpdate);
        deviceEnumerator.RegisterEndpointNotificationCallback(notifications);
        input.SelectedIndexChanged += (_, _) =>
        {
            if (!updating) { pendingAutoStart = false; ConfigureInput(0); }
        };
        cable.SelectedIndexChanged += (_, _) =>
        {
            if (!updating) { pendingAutoStart = false; UpdateCable(); }
        };
        exclusiveOutput.CheckedChanged += (_, _) =>
        {
            outputBits.Enabled = exclusiveOutput.Checked && bridge is null;
            if (!updating)
                pendingAutoStart = false;
        };
        outputBits.SelectedIndexChanged += (_, _) => { if (!updating) pendingAutoStart = false; };
        channel.SelectedIndexChanged += (_, _) =>
        {
            if (!updating) { pendingAutoStart = false; UpdateStartButton(); }
        };
        gain.ValueChanged += (_, _) =>
        {
            gainLabel.Text = Texts.Get("GainValue", gain.Value);
            bridge?.Provider?.SetGain(gain.Value);
        };
        start.Click += (_, _) => ToggleBridge();
        meterTimer.Tick += (_, _) => UpdateMeter();
        deviceTimer.Tick += (_, _) => { deviceTimer.Stop(); RefreshDevices(); };
        Shown += async (_, _) =>
        {
            if (initialized)
                return;
            initialized = true;
            if (WindowState == FormWindowState.Normal)
            {
                var screen = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, screen.Width), Math.Min(MinimumSize.Height, screen.Height));
                Size = new Size(Math.Min(Width, screen.Width), Math.Min(Height, screen.Height));
            }
            var loaded = LoadSettings();
            pendingAutoStart = autoStart && loaded && settings.InputId is not null && settings.CableId is not null;
            RefreshDevices();
            if (!loaded)
                SetStatus(Texts.Get("SettingsError"), true);
            try
            {
                updating = true;
                windowsStartup.Checked = StartupRegistration.IsEnabled();
            }
            catch (Exception error) { AppLog.Write("Cannot read Windows startup registration", error); SetStatus(Texts.Get("StartupError"), true); }
            finally { updating = false; }
            if (background && loaded && settings.InputId is not null && settings.CableId is not null)
                PostToUi(Hide);
            else if (background)
                PostToUi(OpenWindow);
            if (checkUpdates)
                await CheckForUpdatesAsync();
        };
        VisibleChanged += (_, _) => meterTimer.Interval = Visible ? 200 : 1000;
        Resize += (_, _) =>
        {
            // Let WinForms finish storing the normal window bounds before hiding a minimized window.
            if (initialized && WindowState == FormWindowState.Minimized)
                PostToUi(() => { if (WindowState == FormWindowState.Minimized) Hide(); });
        };
        Activated += (_, _) => { if (IsHandleCreated && !updating && bridge is null) ScheduleDeviceUpdate(); };
        FormClosing += (_, args) =>
        {
            if (!exitRequested && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
                return;
            }
            closing = true;
            updateCancellation.Cancel();
            StopBridge();
        };
        FormClosed += (_, _) =>
        {
            meterTimer.Dispose();
            deviceTimer.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            deviceEnumerator.UnregisterEndpointNotificationCallback(notifications);
            deviceEnumerator.Dispose();
            updateClient.Dispose();
            updateCancellation.Dispose();
        };
        ResumeLayout(true);
        if (background)
        {
            firstBackgroundOpen = true;
            ShowInTaskbar = false;
            _ = Handle; // Complete initial DPI scaling before Windows minimizes the window.
            WindowState = FormWindowState.Minimized;
        }
    }

    private void RefreshDevices()
    {
        if (closing)
            return;
        DeviceRefreshCount++;
        try
        {
            var next = DeviceCatalog.Read();
            var interrupted = false;
            if (bridge is not null)
            {
                var selectedInput = (InputChoice)input.SelectedItem!;
                var selectedCable = (CableChoice)cable.SelectedItem!;
                if (!next.Inputs.Contains(selectedInput) || !next.Cables.Contains(selectedCable))
                {
                    ShowError(Texts.Get("DeviceChanged"), new InvalidOperationException("An active audio endpoint was removed or reconfigured."));
                    interrupted = true;
                }
                else
                    return;
            }
            var selectedId = (input.SelectedItem as InputChoice)?.Id ?? settings.InputId;
            var selectedBackend = (input.SelectedItem as InputChoice)?.Backend ?? settings.Backend;
            var selectedCableId = (cable.SelectedItem as CableChoice)?.Render.Id ?? settings.CableId;
            var selectedChannel = channel.SelectedIndex >= 0 ? channel.SelectedIndex : settings.Channel;
            updating = true;
            input.Items.Clear();
            input.Items.AddRange(next.Inputs);
            cable.Items.Clear();
            cable.Items.AddRange(next.Cables);
            input.SelectedItem = selectedId is null
                ? next.Inputs.FirstOrDefault(d => d.Name.Contains("Apollo", StringComparison.OrdinalIgnoreCase)) ?? next.Inputs.FirstOrDefault()
                : next.Inputs.FirstOrDefault(d => d.Id == selectedId && d.Backend == selectedBackend);
            cable.SelectedItem = selectedCableId is null ? next.Cables.FirstOrDefault()
                : next.Cables.FirstOrDefault(d => d.Render.Id == selectedCableId);
            updating = false;
            ConfigureInput(selectedChannel);
            UpdateCable();
            if (input.SelectedItem is null)
                SetStatus(Texts.Get(selectedId is null ? "NoInput" : "SavedInputMissing"), true);
            else if (cable.SelectedItem is null)
                SetStatus(Texts.Get("NoCable"), true);
            if (interrupted)
                SetStatus(Texts.Get("DeviceChanged"), true);
            if (pendingAutoStart && start.Enabled)
            {
                pendingAutoStart = false;
                ToggleBridge();
            }
        }
        catch (Exception error)
        {
            updating = false;
            ShowError(Texts.Get("DeviceListError"), error);
        }
    }

    private void ConfigureInput(int selectedChannel)
    {
        inputReady = false;
        updating = true;
        channel.Items.Clear();
        var choice = input.SelectedItem as InputChoice;
        asioRate.Visible = choice?.Backend == InputBackend.Asio;
        try
        {
            if (choice is null)
            {
                inputFormat.Text = Texts.Get("SelectInput");
                return;
            }
            var names = choice.Backend == InputBackend.Asio ? DeviceCatalog.ReadAsioChannels(choice.Id)
                : Enumerable.Range(0, choice.Channels).Select(i => Texts.Get("ChannelNumber", i + 1)).ToArray();
            channel.Items.AddRange(names);
            channel.SelectedIndex = selectedChannel < names.Length ? selectedChannel : -1;
            inputReady = names.Length > 0;
            inputFormat.Text = choice.Backend == InputBackend.Asio ? Texts.Get("AsioFormat", names.Length)
                : Texts.Get("WindowsFormat", choice.Channels, choice.SampleRate, choice.Bits);
            if (inputReady)
                SetStatus(Texts.Get("Ready"), false);
            else
                SetStatus(Texts.Get("NoChannels"), true);
        }
        catch (Exception error)
        {
            ShowError(Texts.Get("InputProbeError"), error);
        }
        finally
        {
            updating = false;
            UpdateStartButton();
        }
    }

    private void UpdateCable()
    {
        var selected = cable.SelectedItem as CableChoice;
        outputFormat.Text = selected?.Capture is { } endpoint
            ? Texts.Get("CableFormat", endpoint.Name, endpoint.Channels, endpoint.SampleRate)
            : Texts.Get("SelectCable");
        if (selected is not null)
        {
            try { DeviceCatalog.ValidateCable(selected); }
            catch (Exception error) { SetStatus(error.Message, true); }
        }
        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        start.Text = Texts.Get(bridge is null ? "Start" : "Stop");
        start.Enabled = !installingUpdate && !cleanupFailed && (bridge is not null || (inputReady && input.SelectedItem is not null && channel.SelectedIndex >= 0
            && cable.SelectedItem is CableChoice { Capture.Channels: >= 1 and <= 2, Render.Channels: 2 }));
        trayStart.Text = start.Text;
        trayStart.Enabled = start.Enabled;
        tray.Text = Texts.Get(bridge is null ? "TrayStopped" : "TrayRunning");
    }

    private void ToggleBridge()
    {
        pendingAutoStart = false;
        if (bridge is not null)
        {
            if (StopBridge() && !settingsSaveFailed)
                SetStatus(Texts.Get("Stopped"), false);
            return;
        }
        start.Enabled = false;
        try
        {
            var selectedInput = (InputChoice)input.SelectedItem!;
            var selectedCable = (CableChoice)cable.SelectedItem!;
            // Re-read endpoint formats at start, including changes made in Windows Sound settings.
            var current = DeviceCatalog.Read();
            selectedInput = current.Inputs.FirstOrDefault(d => d.Id == selectedInput.Id && d.Backend == selectedInput.Backend)
                ?? throw new InvalidOperationException(Texts.Get("DeviceChanged"));
            selectedCable = current.Cables.FirstOrDefault(d => d.Render.Id == selectedCable.Render.Id)
                ?? throw new InvalidOperationException(Texts.Get("DeviceChanged"));
            bridge = new AudioBridge(selectedInput, selectedCable, channel.SelectedIndex,
                (int)asioRate.SelectedItem!, (int)reserve.SelectedItem!, exclusiveOutput.Checked, (int)outputBits.SelectedItem!);
            var session = bridge;
            bridge.Failed += error =>
            {
                PostToUi(() => { if (bridge == session) ShowError(Texts.Get("StreamError"), error); });
            };
            bridge.Start(gain.Value);
            input.Enabled = channel.Enabled = cable.Enabled = asioRate.Enabled = reserve.Enabled = exclusiveOutput.Enabled = outputBits.Enabled = false;
            if (selectedInput.Backend == InputBackend.Asio)
                inputFormat.Text = Texts.Get("AsioBuffer", bridge.AsioBufferSamples,
                    bridge.AsioBufferSamples * 1000.0 / (int)asioRate.SelectedItem!);
            startedAt = lastCaptureAt = DateTime.UtcNow;
            runningMessage = Texts.Get("Running", selectedCable.Capture!.Name);
            if (exclusiveOutput.Checked)
                runningMessage += "\n\n" + Texts.Get("ExclusiveRunning", (int)outputBits.SelectedItem!, bridge.Provider!.WaveFormat.SampleRate);
            previousCaptured = previousMissing = previousDropped = 0;
            SetStatus(Texts.Get("Starting", selectedCable.Capture!.Name), false);
            SaveSettings();
            meterTimer.Start();
        }
        catch (Exception error)
        {
            ShowError(Texts.Get(exclusiveOutput.Checked ? "ExclusiveStartError" : "StartError"), error);
        }
        UpdateStartButton();
    }

    private bool StopBridge()
    {
        if (bridge is not null && meterTimer.Enabled)
            SaveSettings();
        meterTimer.Stop();
        var session = bridge;
        bridge = null;
        try { session?.Dispose(); }
        catch (Exception error)
        {
            cleanupFailed = true;
            AppLog.Write("Audio cleanup failed", error);
            SetStatus(Texts.Get("CleanupError") + "\n" + error.Message, true);
        }
        input.Enabled = channel.Enabled = cable.Enabled = asioRate.Enabled = reserve.Enabled = exclusiveOutput.Enabled = true;
        outputBits.Enabled = exclusiveOutput.Checked;
        meter.Value = 0;
        meterLabel.Text = Texts.Get("NoLevel");
        UpdateStartButton();
        return !cleanupFailed;
    }

    private void UpdateMeter()
    {
        if (bridge?.Provider is not { } provider)
            return;
        var metrics = provider.ReadMetrics();
        if (Visible)
        {
            meter.Value = (int)Math.Clamp(metrics.Peak * 1000, 0, 1000);
            var level = metrics.Peak <= 0.00001f ? Texts.Get("Silent") : Texts.Get("Decibels", 20 * Math.Log10(metrics.Peak));
            if (meterLabel.Text != level)
                meterLabel.Text = level;
        }
        if (metrics.CapturedFrames != previousCaptured)
            lastCaptureAt = DateTime.UtcNow;
        if ((DateTime.UtcNow - lastCaptureAt).TotalSeconds > 3)
        {
            ShowError(Texts.Get("NoData"), new InvalidOperationException("The capture driver has not supplied audio frames for three seconds."));
            return;
        }
        if (metrics.CapturedFrames != 0)
        {
            if (metrics.Peak >= 1)
                SetStatus(Texts.Get("Clipping"), true);
            else if (metrics.InvalidSamples > 0)
                SetStatus(Texts.Get("InvalidSamples"), true);
            else if ((DateTime.UtcNow - startedAt).TotalSeconds > 2 &&
                (metrics.MissingFrames > previousMissing || metrics.DroppedFrames > previousDropped))
                SetStatus(Texts.Get("BufferWarning"), true);
            else
                SetStatus(settingsSaveFailed ? Texts.Get("SettingsSaveError") : runningMessage, settingsSaveFailed);
        }
        previousCaptured = metrics.CapturedFrames;
        previousMissing = metrics.MissingFrames;
        previousDropped = metrics.DroppedFrames;
    }

    private void SetStatus(string text, bool error)
    {
        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (fullStatus != text)
        {
            fullStatus = text;
            LayoutStatusText();
        }
        statusHeading.Text = Texts.Get(error ? "StatusAttention" : bridge is null ? "StatusStopped" : "StatusActive");
        var color = error ? ErrorInk : ForeColor;
        statusHeading.ForeColor = color;
        if (status.ForeColor != color)
            status.ForeColor = color;
    }

    private void ShowError(string text, Exception error)
    {
        AppLog.Write("Audio operation failed", error);
        StopBridge();
        var advice = error.HResult == unchecked((int)0x80070005) ? Texts.Get("MicrophonePermission") : "";
        SetStatus(text + "\n\n" + advice + error.Message, true);
        if (!Visible && !closing)
            tray.ShowBalloonTip(6000, Texts.Get("Title"), text, ToolTipIcon.Error);
    }

    private void ScheduleDeviceUpdate()
    {
        Interlocked.Increment(ref deviceUpdateRequests);
        PostToUi(() => { deviceTimer.Stop(); deviceTimer.Start(); });
    }

    internal void PostToUi(Action action)
    {
        if (closing || !IsHandleCreated)
            return;
        try { BeginInvoke((Action)(() => { if (!closing) action(); })); }
        catch (InvalidOperationException) when (closing || IsDisposed) { /* The window closed while the callback was queued. */ }
    }

    internal void OpenWindow()
    {
        ShowInTaskbar = true;
        Show();
        // A hidden form only caches WindowState. Restore after showing so Windows restores its bounds.
        WindowState = FormWindowState.Normal;
        if (firstBackgroundOpen)
        {
            // A window first created minimized has no normal bounds at the display's DPI.
            firstBackgroundOpen = false;
            ClientSize = new Size(DefaultClientSize.Width * DeviceDpi / 96, DefaultClientSize.Height * DeviceDpi / 96);
        }
        Activate();
    }

    internal void RequestExit()
    {
        exitRequested = true;
        Close();
    }

    internal void VerifyShell()
    {
        var previousSize = Size;
        var previousMinimumSize = MinimumSize;
        // Exercise the runner's compact 720p work area on larger developer displays too.
        MinimumSize = new Size(MinimumSize.Width, Math.Min(MinimumSize.Height, 720 * DeviceDpi / 96));
        Size = MinimumSize;
        PerformLayout();
        if (status.Height < status.Font.Height * 3 || !ClientRectangle.Contains(RectangleToClient(status.RectangleToScreen(status.ClientRectangle))))
            throw new InvalidOperationException($"Audio status area is clipped at the minimum window size: status={status.Bounds}, clientBounds={RectangleToClient(status.RectangleToScreen(status.ClientRectangle))}, size={Size}, client={ClientRectangle}, minimum={MinimumSize}, fontHeight={status.Font.Height}, DPI={DeviceDpi}.");
        foreach (var control in new Control[] { input, channel, cable, reserve, outputBits, gain, meter, windowsStartup, exclusiveOutput, start, updateAction })
        {
            var bounds = control.RectangleToScreen(control.ClientRectangle);
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                    throw new InvalidOperationException($"Control {control.AccessibleName ?? control.Text} is clipped at the minimum window size: {bounds}, preferred={control.PreferredSize}, autoSize={control.AutoSize}, parent {parent.GetType().Name} {parent.RectangleToScreen(parent.ClientRectangle)}, preferred={parent.PreferredSize}.");
        }
        MinimumSize = previousMinimumSize;
        Size = previousSize;
        if (exclusiveOutput.Checked != settings.ExclusiveOutput || !exclusiveOutput.Enabled)
            throw new InvalidOperationException("Saved cable output mode is not available in the native control.");
        if ((int)outputBits.SelectedItem! != settings.OutputBits || outputBits.Enabled != exclusiveOutput.Checked)
            throw new InvalidOperationException("Saved PCM bit depth is not available in the native control.");
        if (input.SelectedItem is not null && (input.Text.Length == 0 || channel.Text.Length == 0))
            throw new InvalidOperationException("Selected input or microphone channel is not visible in the native control.");
        Close();
        if (Visible || IsDisposed || !tray.Visible || meterTimer.Interval != 1000)
            throw new InvalidOperationException("Background tray state is incorrect.");
        OpenWindow();
        if (!Visible || meterTimer.Interval != 200)
            throw new InvalidOperationException("Window could not be restored from the tray.");
        WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        if (Visible)
            throw new InvalidOperationException("Minimizing did not hide the application in the tray.");
        OpenWindow();
        Application.DoEvents();
    }

    private bool LoadSettings()
    {
        try
        {
            settings = SettingsStore.Load(SettingsStore.FilePath);
            gain.Value = settings.Gain;
            gainLabel.Text = Texts.Get("GainValue", gain.Value);
            meterLabel.Text = Texts.Get("NoLevel");
            asioRate.SelectedItem = settings.AsioRate;
            reserve.SelectedItem = settings.ReserveMilliseconds;
            exclusiveOutput.Checked = settings.ExclusiveOutput;
            outputBits.SelectedItem = settings.OutputBits;
            return true;
        }
        catch (Exception error)
        {
            AppLog.Write("Cannot load settings", error);
            settings = new UserSettings(InputBackend.Wasapi, null, null);
            SetStatus(Texts.Get("SettingsError"), true);
            return false;
        }
    }

    private void SaveSettings()
    {
        var selectedInput = (InputChoice)input.SelectedItem!;
        var selectedCable = (CableChoice)cable.SelectedItem!;
        settings = new UserSettings(selectedInput.Backend, selectedInput.Id, selectedCable.Render.Id,
            channel.SelectedIndex, gain.Value, (int)asioRate.SelectedItem!, (int)reserve.SelectedItem!, exclusiveOutput.Checked,
            (int)outputBits.SelectedItem!);
        try
        {
            SettingsStore.Save(SettingsStore.FilePath, settings);
            settingsSaveFailed = false;
        }
        catch (Exception error)
        {
            AppLog.Write("Cannot save settings", error);
            settingsSaveFailed = true;
            SetStatus(Texts.Get("SettingsSaveError"), true);
        }
    }

    private void OpenSoundSettings()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); }
        catch (Exception error) { AppLog.Write("Cannot open Windows Sound", error); SetStatus(Texts.Get("SoundSettingsError"), true); }
    }
}

internal sealed class DeviceNotifications(Action changed) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { changed(); }
    public void OnDeviceAdded(string deviceId) { changed(); }
    public void OnDeviceRemoved(string deviceId) { changed(); }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { changed(); }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { changed(); }
}
