using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApolloMicBridge;

internal sealed record ReleaseVersion(Version Number, string Prerelease) : IComparable<ReleaseVersion>
{
    internal static ReleaseVersion Parse(string tag)
    {
        if (tag.Length > 128)
            throw new FormatException("Release tag is too long.");
        var match = Regex.Match(tag, @"^v?(\d+\.\d+\.\d+(?:\.\d+)?)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var number))
            throw new FormatException("Release tag must contain a version such as v0.2.0 or v0.2.0-beta.1.");
        return new ReleaseVersion(new Version(number.Major, number.Minor, number.Build, Math.Max(0, number.Revision)), match.Groups[2].Value);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;
        var result = Number.CompareTo(other.Number);
        if (result != 0 || Prerelease == other.Prerelease)
            return result;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return Prerelease.Length == 0 ? 1 : -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = left[i].All(char.IsAsciiDigit);
            var rightNumeric = right[i].All(char.IsAsciiDigit);
            if (leftNumeric && rightNumeric)
            {
                var a = left[i].TrimStart('0');
                var b = right[i].TrimStart('0');
                result = a.Length.CompareTo(b.Length);
                if (result == 0)
                    result = string.CompareOrdinal(a, b);
            }
            else
                result = leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (result != 0)
                return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}

internal sealed record ReleaseUpdate(string Tag, ReleaseVersion Version, string AssetName, Uri DownloadUrl, long Size, string Sha256);

internal sealed class UpdateClient : IDisposable
{
    internal const string Repository = "LeCyreaxYT/fivem-mic-distortion-fix";
    internal static readonly string CurrentVersion = typeof(UpdateClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private const long MaximumPackageSize = 256L * 1024 * 1024;
    private readonly HttpClient http;

    internal UpdateClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ApolloMicBridge/" + CurrentVersion);
    }

    internal async Task<ReleaseUpdate?> CheckAsync(bool installed, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // The latest endpoint excludes prereleases. This app deliberately includes published previews.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" + Repository + "/releases?per_page=100");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(Texts.Get("UpdateRateLimit"), null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return SelectRelease(json.RootElement, ReleaseVersion.Parse(CurrentVersion), installed);
    }

    internal static ReleaseUpdate? SelectRelease(JsonElement releases, ReleaseVersion current, bool installed)
    {
        var candidates = new List<(JsonElement Json, string Tag, ReleaseVersion Version)>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
                continue;
            var tag = release.GetProperty("tag_name").GetString()!;
            ReleaseVersion version;
            try { version = ReleaseVersion.Parse(tag); }
            catch (FormatException error) { AppLog.Write("Skipping release with unsupported version tag: " + tag, error); continue; }
            if (version.CompareTo(current) > 0)
                candidates.Add((release, tag, version));
        }
        if (candidates.Count == 0)
            return null;
        var latest = candidates.MaxBy(candidate => candidate.Version);
        var expectedName = installed
            ? "ApolloMicBridge-Setup-" + latest.Tag.TrimStart('v') + ".exe"
            : "ApolloMicBridge-Portable-win-x64.zip";
        var matching = latest.Json.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == expectedName).ToArray();
        if (matching.Length != 1)
            throw new InvalidDataException(Texts.Get("UpdatePackageMissing", latest.Tag));
        var asset = matching[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (size <= 0 || size > MaximumPackageSize || digest is null ||
            !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(Texts.Get("UpdatePackageInvalid"));
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
        if (url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !url.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException(Texts.Get("UpdatePackageInvalid"));
        return new ReleaseUpdate(latest.Tag, latest.Version, expectedName, url, size, digest[7..]);
    }

    internal async Task DownloadAsync(ReleaseUpdate update, string destination, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var partial = destination + ".download";
        try
        {
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException(Texts.Get("UpdatePackageInvalid"));
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long received = 0;
                var previousPercent = -1;
                int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumPackageSize)
                        throw new InvalidDataException(Texts.Get("UpdatePackageInvalid"));
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    var percent = (int)(received * 100 / update.Size);
                    if (percent != previousPercent)
                    {
                        progress.Report(percent);
                        previousPercent = percent;
                    }
                }
                if (received != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Texts.Get("UpdatePackageInvalid"));
            }
            File.Move(partial, destination, true);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public void Dispose() => http.Dispose();
}
