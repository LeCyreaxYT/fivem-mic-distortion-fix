using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ApolloMicBridge;

internal static class UpdateTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("release versions compare numerically, including previews and stable promotions", Versions);
        test("published prereleases are included; drafts, older and equal versions are ignored", Releases);
        test("matching Windows assets require a trusted URL, size and SHA-256 digest", Assets);
        test("release checks handle empty results, rate limits, offline and cancellation", CheckFailures);
        test("downloads verify bytes before replacing any existing package", Downloads);
        test("portable updates preserve unrelated files and reject traversal or missing payloads", Portable);
        test("portable updates roll back earlier replacements when a later file is locked", Rollback);
    }

    private static void Versions()
    {
        Assert(ReleaseVersion.Parse("v0.2.0").CompareTo(ReleaseVersion.Parse("0.2.0.0")) == 0, "Equivalent numeric versions");
        Assert(ReleaseVersion.Parse("0.10.0").CompareTo(ReleaseVersion.Parse("0.9.9")) > 0, "Numeric version order");
        Assert(ReleaseVersion.Parse("0.3.0-beta.10").CompareTo(ReleaseVersion.Parse("0.3.0-beta.2")) > 0, "Numeric preview order");
        Assert(ReleaseVersion.Parse("0.3.0").CompareTo(ReleaseVersion.Parse("0.3.0-rc.1")) > 0, "Stable promotion");
        Assert(ReleaseVersion.Parse("0.3.0+build.7").CompareTo(ReleaseVersion.Parse("0.3.0")) == 0, "Build metadata");
        Reject<FormatException>(() => ReleaseVersion.Parse("nightly"));
        Reject<FormatException>(() => ReleaseVersion.Parse("1.2"));
    }

    private static JsonElement Release(string tag, bool draft = false, bool preview = true, string? url = null,
        string? digest = null, long size = 3, bool missing = false)
    {
        var portable = "ApolloMicBridge-Portable-win-x64.zip";
        var setup = "ApolloMicBridge-Setup-" + tag.TrimStart('v') + ".exe";
        return JsonSerializer.SerializeToElement(new
        {
            tag_name = tag, draft, prerelease = preview,
            assets = (missing ? Array.Empty<string>() : new[] { portable, setup }).Select(name => new
            {
                name, size, digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })),
                browser_download_url = url ?? "https://github.com/" + UpdateClient.Repository + "/releases/download/" + tag + "/" + name
            })
        });
    }

    private static ReleaseUpdate? Select(params JsonElement[] releases)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(releases));
        return UpdateClient.SelectRelease(document.RootElement, ReleaseVersion.Parse("0.2.0"), false);
    }

    private static void Releases()
    {
        Assert(Select(Release("0.1.2"), Release("0.2.0"), Release("2.0.0", draft: true)) is null, "No downgrade or draft");
        Assert(Select(Release("v0.3.0-beta.2"), Release("0.2.1", preview: false))?.Tag == "v0.3.0-beta.2", "Preview selected");
        Assert(Select(Release("0.3.0-beta.2"), Release("0.3.0", preview: false))?.Tag == "0.3.0", "Stable selected");
        Assert(Select() is null, "No releases");
        Reject<InvalidDataException>(() => Select(Release("0.4.0", missing: true), Release("0.3.0")));
    }

    private static void Assets()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("v0.3.0") }));
        var installed = UpdateClient.SelectRelease(document.RootElement, ReleaseVersion.Parse("0.2.0"), true)!;
        Assert(installed.AssetName == "ApolloMicBridge-Setup-0.3.0.exe", "Setup asset");
        Assert(Select(Release("0.3.0"))!.AssetName.EndsWith(".zip"), "Portable asset");
        foreach (var url in new[] { "http://github.com/file", "https://github.com.evil.test/file", "https://github.com/other/repo/releases/download/v1/app.exe" })
            Reject<InvalidDataException>(() => Select(Release("0.3.0", url: url)));
        Reject<InvalidDataException>(() => Select(Release("0.3.0", digest: "sha256:123")));
        Reject<InvalidDataException>(() => Select(Release("0.3.0", size: 0)));
        Reject<InvalidDataException>(() => Select(Release("0.3.0", size: 300L * 1024 * 1024)));
    }

    private static void CheckFailures()
    {
        using (var client = new UpdateClient(new Handler((request, _) =>
        {
            Assert(request.Headers.UserAgent.Any(), "User agent");
            Assert(request.RequestUri!.AbsoluteUri.EndsWith("/releases?per_page=100"), "Preview-capable API");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        })))
            Assert(client.CheckAsync(false, default).GetAwaiter().GetResult() is null, "Empty response");
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
        {
            using var client = new UpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
            Reject<HttpRequestException>(() => client.CheckAsync(false, default).GetAwaiter().GetResult());
        }
        using (var offline = new UpdateClient(new Handler((_, _) => throw new HttpRequestException("Offline fixture"))))
            Reject<HttpRequestException>(() => offline.CheckAsync(false, default).GetAwaiter().GetResult());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelled = new UpdateClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
        Reject<OperationCanceledException>(() => cancelled.CheckAsync(false, cancellation.Token).GetAwaiter().GetResult());
    }

    private static void Downloads()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "package.zip");
        try
        {
            var update = Select(Release("0.3.0"))!;
            using (var client = new UpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                       { Content = new ByteArrayContent([1, 2, 3]) }))))
                client.DownloadAsync(update, path, new InlineProgress(), default).GetAwaiter().GetResult();
            Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Verified download");
            foreach (var data in new byte[][] { [1, 2, 4], [1, 2], [1, 2, 3, 4] })
            {
                using var client = new UpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(data) })));
                Reject<InvalidDataException>(() => client.DownloadAsync(update, path, new InlineProgress(), default).GetAwaiter().GetResult());
                Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Previous package preserved");
                Assert(!File.Exists(path + ".download"), "Partial file removed");
            }
            using var cancellation = new CancellationTokenSource();
            using var cancelled = new UpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent([1, 2, 3]) })));
            Reject<OperationCanceledException>(() => cancelled.DownloadAsync(update, path,
                new InlineProgress(_ => cancellation.Cancel()), cancellation.Token).GetAwaiter().GetResult());
            Assert(!File.Exists(path + ".download"), "Cancelled partial removed");
        }
        finally { DeleteTestDirectory(directory); }
    }

    private static void Package(string path, params string[] names)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("new " + name);
        }
    }

    private static void Portable()
    {
        var directory = NewDirectory();
        try
        {
            var executable = Path.Combine(directory, "ApolloMicBridge.exe");
            File.WriteAllText(executable, "old executable");
            File.WriteAllText(Path.Combine(directory, "settings.json"), "user settings");
            var zip = Path.Combine(directory, "package.zip");
            Package(zip, "ApolloMicBridge.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt");
            UpdateInstaller.ApplyPortable(zip, executable);
            Assert(File.ReadAllText(executable) == "new ApolloMicBridge.exe", "Replaced executable");
            Assert(File.ReadAllText(Path.Combine(directory, "settings.json")) == "user settings", "Preserved settings");
            Assert(!Directory.EnumerateFiles(directory, "*.update-*").Any(), "No transaction leftovers");
            foreach (var entries in new[] { new[] { "ApolloMicBridge.exe" }, new[] { "../escaped.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt" },
                         new[] { "ApolloMicBridge.exe", "ApolloMicBridge.exe", "LICENSE" } })
            {
                File.Delete(zip);
                Package(zip, entries);
                Reject<InvalidDataException>(() => UpdateInstaller.ApplyPortable(zip, executable));
                Assert(File.ReadAllText(executable) == "new ApolloMicBridge.exe", "Invalid package did not replace executable");
            }
        }
        finally { DeleteTestDirectory(directory); }
    }

    private static void Rollback()
    {
        var directory = NewDirectory();
        try
        {
            var executable = Path.Combine(directory, "ApolloMicBridge.exe");
            var license = Path.Combine(directory, "LICENSE");
            File.WriteAllText(executable, "old executable");
            File.WriteAllText(license, "old license");
            var zip = Path.Combine(directory, "package.zip");
            Package(zip, "ApolloMicBridge.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt");
            using (File.Open(license, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject<IOException>(() => UpdateInstaller.ApplyPortable(zip, executable));
            Assert(File.ReadAllText(executable) == "old executable", "Executable rolled back");
            Assert(File.ReadAllText(license) == "old license", "Locked file unchanged");
            Assert(!Directory.EnumerateFiles(directory, "*.update-*").Any(), "Rollback cleaned up staging");
        }
        finally { DeleteTestDirectory(directory); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ApolloMicBridge.UpdateTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        // Test fixtures have no subdirectories; remove only files under this generated test directory.
        foreach (var path in Directory.EnumerateFiles(directory))
            File.Delete(path);
        Directory.Delete(directory);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return response(request, cancellationToken);
        }
    }

    private sealed class InlineProgress(Action<int>? report = null) : IProgress<int>
    {
        public void Report(int value)
        {
            Assert(value is >= 0 and <= 100, "Download percentage");
            report?.Invoke(value);
        }
    }
}
