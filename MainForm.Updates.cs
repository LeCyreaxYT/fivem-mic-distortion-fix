using System.Net;
using System.Net.Http;
using static ApolloMicBridge.SkyTheme;

namespace ApolloMicBridge;

internal sealed partial class MainForm
{
    private readonly UpdateClient updateClient = new();
    private readonly CancellationTokenSource updateCancellation = new();
    private ReleaseUpdate? availableUpdate;
    private bool checkingUpdate;
    private bool installingUpdate;

    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdate || installingUpdate || closing)
            return;
        checkingUpdate = true;
        updateAction.Enabled = false;
        updateStatus.Text = Texts.Get("UpdateChecking");
        updateStatus.ForeColor = Muted;
        try
        {
            availableUpdate = await updateClient.CheckAsync(UpdateInstaller.IsInstalled(Application.ExecutablePath), updateCancellation.Token);
            if (closing)
                return;
            updateStatus.Text = availableUpdate is null ? Texts.Get("UpdateCurrent") : Texts.Get("UpdateAvailable", availableUpdate.Tag);
            updateAction.Text = Texts.Get(availableUpdate is null ? "CheckUpdates" : "InstallUpdate");
            if (availableUpdate is not null && !Visible)
                tray.ShowBalloonTip(5000, Texts.Get("Title"), updateStatus.Text, ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            AppLog.Write("Release check failed", error);
            if (!closing)
            {
                updateStatus.Text = error is InvalidDataException ? error.Message :
                    Texts.Get(error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
                        ? "UpdateRateLimit" : "UpdateCheckFailed");
                updateStatus.ForeColor = ErrorInk;
                updateAction.Text = Texts.Get("RetryUpdate");
            }
        }
        finally
        {
            checkingUpdate = false;
            if (!closing)
                updateAction.Enabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (availableUpdate is null || installingUpdate || closing)
            return;
        installingUpdate = true;
        updateAction.Enabled = false;
        updateProgress.Visible = true;
        updateProgress.Value = 0;
        updateStatus.ForeColor = Muted;
        updateStatus.Text = Texts.Get("UpdateDownloading", 0);
        // Keep current audio running during the download; reserve controls for the restart.
        UpdateStartButton();
        try
        {
            var installed = UpdateInstaller.IsInstalled(Application.ExecutablePath);
            var package = UpdateInstaller.Prepare(Application.ExecutablePath, installed);
            var progress = new Progress<int>(percent =>
            {
                if (closing || !installingUpdate)
                    return;
                updateProgress.Value = percent;
                updateStatus.Text = Texts.Get("UpdateDownloading", percent);
            });
            await updateClient.DownloadAsync(availableUpdate, package, progress, updateCancellation.Token);
            if (closing)
                return;
            updateStatus.Text = Texts.Get("UpdateInstalling");
            var resumeAudio = bridge is not null;
            if (!resumeAudio && input.SelectedItem is not null && cable.SelectedItem is not null && channel.SelectedIndex >= 0)
                SaveSettings();
            if (!StopBridge() || settingsSaveFailed)
                throw new InvalidOperationException("Audio could not be stopped and saved for the update.");
            SetStatus(Texts.Get("Stopped"), false);
            await UpdateInstaller.LaunchAsync(Application.ExecutablePath, installed, availableUpdate.Sha256, resumeAudio, !Visible);
            RequestExit();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            AppLog.Write("Update download or launch failed", error);
            if (!closing)
            {
                updateStatus.Text = Texts.Get("UpdateInstallFailed");
                updateStatus.ForeColor = ErrorInk;
                updateAction.Text = Texts.Get("RetryUpdate");
            }
        }
        finally
        {
            installingUpdate = false;
            if (!closing)
            {
                updateAction.Enabled = true;
                updateProgress.Visible = false;
                UpdateStartButton();
            }
        }
    }
}
