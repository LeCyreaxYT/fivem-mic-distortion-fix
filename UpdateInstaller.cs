using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ApolloMicBridge;

internal static class UpdateInstaller
{
    private const string UpdatingMutex = @"Local\SkySystems.ApolloMicBridge.Updating";
    internal static readonly string StagingDirectory = Path.Combine(AppLog.DirectoryPath, "Update");
    private static readonly string[] PortableFiles = ["ApolloMicBridge.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt"];

    internal static bool IsInstalled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\SkySystems\ApolloMicBridge");
        return key?.GetValue("InstallPath") is string path &&
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUpdating()
    {
        if (!Mutex.TryOpenExisting(UpdatingMutex, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    internal static string Prepare(string executable, bool installed)
    {
        // The release is a self-contained single file. A development apphost cannot be copied alone.
        if (File.Exists(Path.ChangeExtension(executable, ".runtimeconfig.json")))
            throw new InvalidOperationException("Install updates from the packaged application, not a development apphost.");
        var targetDirectory = Path.GetDirectoryName(executable)!;
        var probe = Path.Combine(targetDirectory, ".update-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        Directory.CreateDirectory(StagingDirectory);
        File.Copy(executable, Path.Combine(StagingDirectory, "ApolloMicBridge.Update.exe"), true);
        return Path.Combine(StagingDirectory, installed ? "package.exe" : "package.zip");
    }

    internal static async Task LaunchAsync(string executable, bool installed, string sha256, bool resumeAudio, bool background)
    {
        var readyName = @"Local\SkySystems.ApolloMicBridge.UpdateReady-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var start = new ProcessStartInfo(Path.Combine(StagingDirectory, "ApolloMicBridge.Update.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = StagingDirectory
        };
        foreach (var argument in new[] { "--apply-update", Environment.ProcessId.ToString(), executable,
                     installed ? "installed" : "portable", sha256, resumeAudio ? "1" : "0", background ? "1" : "0", readyName })
            start.ArgumentList.Add(argument);
        using var worker = Process.Start(start) ?? throw new IOException("The update helper did not start.");
        if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10))))
        {
            // The helper cannot modify files while its parent is alive. Stop only this owned helper.
            if (!worker.HasExited)
                worker.Kill();
            throw new IOException("The update helper did not become ready.");
        }
    }

    internal static int Run(string[] args)
    {
        string? executable = null;
        var parentExited = false;
        var success = false;
        var stage = AppContext.BaseDirectory;
        try
        {
            if (args.Length != 8 || !int.TryParse(args[1], out var parentId) || !Path.IsPathFullyQualified(args[2]) ||
                args[3] is not ("installed" or "portable") || args[5] is not ("0" or "1") || args[6] is not ("0" or "1") ||
                !args[7].StartsWith(@"Local\SkySystems.ApolloMicBridge.UpdateReady-", StringComparison.Ordinal))
                throw new ArgumentException("Invalid update helper arguments.");
            executable = Path.GetFullPath(args[2]);
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule!.FileName, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update target does not match its parent application.");
            using var updating = new Mutex(true, UpdatingMutex, out var created);
            if (!created)
                throw new InvalidOperationException("Another updater is already running.");
            using var ready = EventWaitHandle.OpenExisting(args[7]);
            ready.Set();
            if (!parent.WaitForExit(30000))
                throw new IOException("The application did not exit; its files have not been changed.");
            parentExited = true;
            var installed = args[3] == "installed";
            var package = Path.Combine(stage, installed ? "package.exe" : "package.zip");
            using (var file = File.OpenRead(package))
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(args[4], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The staged update checksum changed.");
            if (installed)
            {
                var setup = new ProcessStartInfo(package) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
                             "/DIR=" + Path.GetDirectoryName(executable), "/LOG=" + Path.Combine(stage, "install.log") })
                    setup.ArgumentList.Add(argument);
                using var installer = Process.Start(setup) ?? throw new IOException("The update installer did not start.");
                installer.WaitForExit();
                if (installer.ExitCode != 0)
                    throw new IOException("The update installer returned exit code " + installer.ExitCode + ". See " + Path.Combine(stage, "install.log"));
            }
            else
                ApplyPortable(package, executable);
            success = true;
            try { File.Delete(package); }
            catch (IOException error) { AppLog.Write("Cannot remove completed update package", error); }
        }
        catch (Exception error)
        {
            AppLog.Write("Update installation failed", error);
            SkyDialog.Show(null, Texts.Get("Title"), Texts.Get("UpdateApplyFailed") + "\n\n" + error.Message);
        }
        // The update mutex must be released before a normal instance can start.
        if (parentExited && executable is not null && File.Exists(executable))
        {
            try
            {
                var restart = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
                if (success && args[5] == "1")
                    restart.ArgumentList.Add("--autostart");
                if (success && args[6] == "1")
                    restart.ArgumentList.Add("--background");
                using var restarted = Process.Start(restart);
            }
            catch (Exception error)
            {
                AppLog.Write("Cannot restart after update", error);
                SkyDialog.Show(null, Texts.Get("Title"), Texts.Get("UpdateRestartFailed"));
                return 1;
            }
        }
        return success ? 0 : 1;
    }

    internal static void ApplyPortable(string package, string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var suffix = ".update-" + Guid.NewGuid().ToString("N");
        var staged = new List<(string Target, string New, string Backup)>();
        var replaced = new List<(string Target, string Backup)>();
        try
        {
            using (var archive = ZipFile.OpenRead(package))
            {
                if (archive.Entries.Count != PortableFiles.Length ||
                    archive.Entries.Any(entry => !PortableFiles.Contains(entry.FullName, StringComparer.Ordinal)))
                    throw new InvalidDataException("The portable package must contain only the executable and its two license files.");
                foreach (var name in PortableFiles)
                {
                    var entries = archive.Entries.Where(entry => entry.FullName == name).ToArray();
                    if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > 256L * 1024 * 1024)
                        throw new InvalidDataException("Invalid portable package entry: " + name);
                    var target = name == "ApolloMicBridge.exe" ? executable : Path.Combine(directory, name);
                    var pending = target + suffix;
                    staged.Add((target, pending, target + suffix + ".backup"));
                    entries[0].ExtractToFile(pending);
                }
            }
            foreach (var file in staged)
            {
                if (File.Exists(file.Target))
                    File.Move(file.Target, file.Backup);
                replaced.Add((file.Target, file.Backup));
                File.Move(file.New, file.Target);
            }
        }
        catch
        {
            // Roll back every replaced file; retain a backup if Windows prevents recovery.
            foreach (var file in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    File.Delete(file.Target);
                    if (File.Exists(file.Backup))
                        File.Move(file.Backup, file.Target);
                }
                catch (Exception error) { AppLog.Write("Portable update rollback failed; backup retained at " + file.Backup, error); }
            }
            throw;
        }
        finally
        {
            foreach (var file in staged)
                if (File.Exists(file.New))
                    File.Delete(file.New);
        }
        foreach (var file in staged)
        {
            try { File.Delete(file.Backup); }
            catch (IOException error) { AppLog.Write("Cannot remove completed update backup: " + file.Backup, error); }
        }
    }
}
