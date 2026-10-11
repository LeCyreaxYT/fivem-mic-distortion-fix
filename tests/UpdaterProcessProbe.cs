using System.Security.Cryptography;
using System.Text.Json;
using ApolloMicBridge;

// Published as a single-file test fixture. The real updater replaces this process
// with the packaged application, exercising parent exit, file replacement and restart.
internal static class UpdaterProcessProbe
{
    internal static void Run(string sourcePackage, string report)
    {
        var executable = Environment.ProcessPath!;
        var installed = UpdateInstaller.IsInstalled(executable);
        var package = UpdateInstaller.Prepare(executable, installed);
        File.Copy(sourcePackage, package, true);
        string sha256;
        using (var source = File.OpenRead(package))
            sha256 = Convert.ToHexString(SHA256.HashData(source));
        UpdateInstaller.LaunchAsync(executable, installed, sha256, resumeAudio: false, background: true).GetAwaiter().GetResult();
        if (!UpdateInstaller.IsUpdating())
            throw new InvalidOperationException("The update helper did not acquire its mutex before parent exit.");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Installed = installed, HelperReady = true, Executable = executable }));
    }
}
