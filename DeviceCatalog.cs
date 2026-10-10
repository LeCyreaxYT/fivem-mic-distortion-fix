using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ApolloMicBridge;

internal enum InputBackend { Wasapi, Asio }

internal sealed record InputChoice(InputBackend Backend, string Id, string Name, int Channels, int SampleRate, int Bits)
{
    public override string ToString() => (Backend == InputBackend.Asio ? "ASIO · " : "Windows · ") + Name;
}

internal sealed record EndpointInfo(string Id, string Name, int Channels, int SampleRate, int Bits);

internal sealed record CableChoice(EndpointInfo Render, EndpointInfo? Capture)
{
    public override string ToString() => Render.Name;
}

internal sealed record DeviceSnapshot(InputChoice[] Inputs, CableChoice[] Cables);

internal static class DeviceCatalog
{
    public static DeviceSnapshot Read()
    {
        using var enumerator = new MMDeviceEnumerator();
        var capture = ReadEndpoints(enumerator, DataFlow.Capture);
        var render = ReadEndpoints(enumerator, DataFlow.Render);
        var inputs = capture.Where(e => !IsCable(e.Name)).Select(e =>
            new InputChoice(InputBackend.Wasapi, e.Id, e.Name, e.Channels, e.SampleRate, e.Bits)).ToList();
        inputs.AddRange(AsioOut.GetDriverNames().Select(name =>
            new InputChoice(InputBackend.Asio, name, name, 0, 0, 0)));
        var cables = render.Where(e => IsCable(e.Name)).Select(e =>
            new CableChoice(e, capture.FirstOrDefault(c => IsCable(c.Name) && CableStem(c.Name) == CableStem(e.Name))))
            .ToArray();
        return new DeviceSnapshot(inputs.ToArray(), cables);
    }

    private static List<EndpointInfo> ReadEndpoints(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var result = new List<EndpointInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var format = device.AudioClient.MixFormat;
                result.Add(new EndpointInfo(device.ID, device.FriendlyName, format.Channels, format.SampleRate, format.BitsPerSample));
            }
        }
        return result;
    }

    private static bool IsCable(string name) => name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        && name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static string CableStem(string name)
    {
        var label = name.Split('(')[0];
        return label.Replace("Input", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Output", "", StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
    }

    public static string[] ReadAsioChannels(string driverName)
    {
        using var driver = new AsioOut(driverName);
        return Enumerable.Range(0, driver.DriverInputChannelCount)
            .Select(i => $"{i + 1} · {driver.AsioInputChannelName(i)}").ToArray();
    }

    public static void ValidateCable(CableChoice cable)
    {
        if (cable.Capture is null)
            throw new InvalidOperationException(Texts.Get("MissingCapture"));
        if (cable.Capture.Channels is < 1 or > 2)
            throw new InvalidOperationException(Texts.Get("BadCableChannels", cable.Capture.Channels));
        if (cable.Render.Channels != 2)
            throw new InvalidOperationException(Texts.Get("BadRenderChannels", cable.Render.Channels));
    }
}
