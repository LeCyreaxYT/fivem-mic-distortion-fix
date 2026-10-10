using System.Text.Json;

namespace ApolloMicBridge;

internal sealed record UserSettings(InputBackend Backend, string? InputId, string? CableId,
    int Channel = 0, int Gain = 0, int AsioRate = 48000, int ReserveMilliseconds = 5, bool ExclusiveOutput = false,
    int OutputBits = 16);

internal static class SettingsStore
{
    internal static readonly string FilePath = Path.Combine(AppLog.DirectoryPath, "settings.json");

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path))
            return new UserSettings(InputBackend.Wasapi, null, null);
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Settings file contains no settings object.");
        if (!Enum.IsDefined(settings.Backend) || settings.Channel < 0 || settings.Gain is < -24 or > 12
            || settings.AsioRate is not (44100 or 48000 or 96000) || settings.ReserveMilliseconds is not (5 or 10 or 20)
            || settings.OutputBits is not (16 or 24))
            throw new InvalidDataException("Settings contain unsupported audio values.");
        return settings;
    }

    public static void Save(string path, UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, Program.JsonOptions));
        File.Move(path + ".tmp", path, true);
    }
}
