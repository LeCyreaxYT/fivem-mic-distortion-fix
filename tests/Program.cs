using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ApolloMicBridge;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class Program
{
    private static int passed;

    private static int Main(string[] args)
    {
        try
        {
            Test("one, two, four, ten and 32 channel float capture", ChannelSelection);
            Test("PCM 8, 16, 24 and 32 bit normalization", PcmFormats);
            Test("float64 normalization", Float64Format);
            Test("invalid channel and partial frame rejection", InvalidFrames);
            Test("FIFO order, bounded overflow and silent underrun", QueueBehavior);
            Test("finite output, gain and clipping", GainAndInvalidSamples);
            Test("identical left and right output", StereoOutput);
            Test("exclusive PCM16 output keeps microphone signal and stereo channels", ExclusivePcmOutput);
            Test("exclusive driver accepts extended stereo descriptor while rejecting basic PCM", ExclusiveFormatNegotiation);
            Test("24-bit stereo at 44.1 and 48 kHz preserves signed PCM, clipping and buffer boundaries", ExclusivePcm24Output);
            Test("short buffer priming without consuming partial audio", BufferPriming);
            Test("independent device clocks keep latency bounded", ClockDrift);
            Test("render stalls discard stale backlog and restore short latency", RenderStall);
            Test("44.1, 48 and 96 kHz resampling", Resampling);
            Test("virtual microphone channel validation", CableValidation);
            Test("start failure cleanup and repeatable disposal", FailedStartCleanup);
            Test("settings persistence and invalid values", SettingsPersistence);
            Test("quoted Windows startup command", StartupCommand);
            if (args.Length == 2 && args[0] is "--benchmark" or "--benchmark-exclusive" or "--benchmark-exclusive24")
                Benchmark(args[1], args[0] switch { "--benchmark-exclusive" => 16, "--benchmark-exclusive24" => 24, _ => 0 });
            Console.WriteLine($"PASS: {passed} meaningful audio checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void ChannelSelection()
    {
        foreach (var channels in new[] { 1, 2, 4, 10, 32 })
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
            var decoder = new PcmChannelDecoder(format);
            var data = new byte[80 * format.BlockAlign];
            for (var frame = 0; frame < 80; frame++)
                for (var channel = 0; channel < channels; channel++)
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(frame * format.BlockAlign + channel * 4),
                        channel / 100f + frame / 1000f);
            var mono = new float[80];
            for (var channel = 0; channel < channels; channel++)
            {
                Equal(80, decoder.Decode(data, channel, mono));
                for (var frame = 0; frame < 80; frame++)
                    Near(channel / 100f + frame / 1000f, mono[frame]);
            }
        }
    }

    private static void PcmFormats()
    {
        foreach (var bits in new[] { 8, 16, 24, 32 })
        {
            var format = new WaveFormat(48000, bits, 10);
            var data = new byte[4 * format.BlockAlign];
            var sampleBytes = bits / 8;
            float[] expected = [-1, -0.5f, 0, 0.5f];
            for (var frame = 0; frame < expected.Length; frame++)
            {
                var sample = data.AsSpan(frame * format.BlockAlign + 7 * sampleBytes, sampleBytes);
                switch (bits)
                {
                    case 8: sample[0] = (byte)(128 + expected[frame] * 128); break;
                    case 16: BinaryPrimitives.WriteInt16LittleEndian(sample, (short)(expected[frame] * 32768)); break;
                    case 24:
                        var value = (int)(expected[frame] * 8388608);
                        sample[0] = (byte)value;
                        sample[1] = (byte)(value >> 8);
                        sample[2] = (byte)(value >> 16);
                        break;
                    case 32: BinaryPrimitives.WriteInt32LittleEndian(sample, (int)(expected[frame] * 2147483648)); break;
                }
            }
            var mono = new float[4];
            new PcmChannelDecoder(format).Decode(data, 7, mono);
            for (var i = 0; i < mono.Length; i++)
                Near(expected[i], mono[i]);
        }
    }

    private static void Float64Format()
    {
        var format = WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 48000, 10, 3840000, 80, 64);
        var data = new byte[160];
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(8 * 8), -0.75);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(80 + 8 * 8), 0.25);
        var mono = new float[2];
        new PcmChannelDecoder(format).Decode(data, 8, mono);
        Near(-0.75f, mono[0]);
        Near(0.25f, mono[1]);
    }

    private static void InvalidFrames()
    {
        var decoder = new PcmChannelDecoder(WaveFormat.CreateIeeeFloatWaveFormat(48000, 10));
        Throws<ArgumentOutOfRangeException>(() => decoder.Decode(new byte[40], 10, new float[1]));
        Throws<ArgumentException>(() => decoder.Decode(new byte[41], 0, new float[2]));
    }

    private static void QueueBehavior()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        var source = Enumerable.Range(0, 20000).Select(i => i / 20000f).ToArray();
        provider.Push(source);
        var mono = new float[16002];
        provider.ReadMono(mono);
        for (var i = 0; i < 16000; i++)
            Near(source[i + 4000], mono[i]);
        Near(0, mono[16000]);
        Near(0, mono[16001]);
        var metrics = provider.ReadMetrics();
        Equal(4000L, metrics.DroppedFrames);
        Equal(2L, metrics.MissingFrames);
        Equal(0, metrics.BufferedMilliseconds);
    }

    private static void GainAndInvalidSamples()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        provider.SetGain(6);
        provider.Push([0.1f, 0.9f, -0.9f, float.NaN, float.PositiveInfinity]);
        var result = new float[5];
        provider.ReadMono(result);
        Near(0.1f * MathF.Pow(10, 6 / 20f), result[0]);
        Near(1, result[1]);
        Near(-1, result[2]);
        Near(0, result[3]);
        Near(0, result[4]);
        var metrics = provider.ReadMetrics();
        Equal(2L, metrics.ClippedSamples);
        Equal(2L, metrics.InvalidSamples);
    }

    private static void StereoOutput()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        provider.Push(Enumerable.Range(0, 4000).Select(i => (float)(0.25 * Math.Sin(i * 2 * Math.PI * 1000 / 48000))).ToArray());
        var output = Enumerable.Repeat(123f, 972).ToArray();
        Equal(960, provider.Read(output, 6, 960));
        for (var i = 6; i < 966; i += 2)
        {
            Equal(output[i], output[i + 1]);
            if (!float.IsFinite(output[i]) || Math.Abs(output[i]) > 1)
                throw new Exception("Invalid output sample.");
        }
        Near(123, output[0]);
        Near(123, output[^1]);
    }

    private static void ExclusivePcmOutput()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        provider.Push(Enumerable.Range(0, 4000).Select(i => (float)(0.25 * Math.Sin(i * 2 * Math.PI * 1000 / 48000))).ToArray());
        IWaveProvider pcm = new ExclusivePcmProvider(provider, 16, format => format.Encoding == WaveFormatEncoding.Extensible);
        Equal((int)WaveFormatEncoding.Extensible, (int)pcm.WaveFormat.Encoding);
        Equal(16, pcm.WaveFormat.BitsPerSample);
        Equal(2, pcm.WaveFormat.Channels);
        Equal(48000, pcm.WaveFormat.SampleRate);
        var output = Enumerable.Repeat((byte)0x5a, 1936).ToArray();
        Equal(1920, pcm.Read(output, 8, 1920));
        var minimum = 0;
        var maximum = 0;
        for (var i = 8; i < 1928; i += 4)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i, 2));
            var right = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i + 2, 2));
            Equal(left, right);
            minimum = Math.Min(minimum, left);
            maximum = Math.Max(maximum, left);
        }
        if (minimum is < -9000 or > -7000 || maximum is < 7000 or > 9000)
            throw new Exception("PCM16 output lost or changed the microphone signal level.");
        if (output.Take(8).Concat(output.Skip(1928)).Any(value => value != 0x5a))
            throw new Exception("PCM16 output wrote outside the requested buffer region.");
    }

    private static void ExclusiveFormatNegotiation()
    {
        foreach (var rate in new[] { 44100, 48000 })
        foreach (var bits in new[] { 16, 24 })
        {
            var source = new LiveAudioProvider(rate, rate, 5);
            var requested = new List<WaveFormat>();
            IWaveProvider extended = new ExclusivePcmProvider(source, bits, format =>
            {
                requested.Add(format);
                return format.Encoding == WaveFormatEncoding.Extensible;
            });
            Equal(2, requested.Count);
            Equal((int)WaveFormatEncoding.Pcm, (int)requested[0].Encoding);
            Equal((int)WaveFormatEncoding.Extensible, (int)extended.WaveFormat.Encoding);
            Equal(rate, extended.WaveFormat.SampleRate);
            Equal(bits, extended.WaveFormat.BitsPerSample);
            Equal(2, extended.WaveFormat.Channels);
            Equal(bits / 8 * 2, extended.WaveFormat.BlockAlign);
            Equal(new Guid("00000001-0000-0010-8000-00aa00389b71"), ((WaveFormatExtensible)extended.WaveFormat).SubFormat);

            var probeCount = 0;
            IWaveProvider basic = new ExclusivePcmProvider(source, bits, _ => { probeCount++; return true; });
            Equal((int)WaveFormatEncoding.Pcm, (int)basic.WaveFormat.Encoding);
            Equal(1, probeCount);
            Throws<NotSupportedException>(() => new ExclusivePcmProvider(source, bits, _ => false));
            Throws<InvalidOperationException>(() => new ExclusivePcmProvider(source, bits, _ =>
                throw new InvalidOperationException("Device disconnected during format negotiation.")));
        }
        Throws<ArgumentOutOfRangeException>(() => new ExclusivePcmProvider(new LiveAudioProvider(48000, 48000), 32, _ => true));
    }

    private static void ExclusivePcm24Output()
    {
        foreach (var rate in new[] { 44100, 48000 })
        foreach (var encoding in new[] { WaveFormatEncoding.Pcm, WaveFormatEncoding.Extensible })
        {
            float[] levels = [0, 0.5f, -0.5f, 1, -1, 2, -2];
            var input = new byte[levels.Length * 8];
            for (var i = 0; i < levels.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(i * 8), levels[i]);
                BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(i * 8 + 4), levels[i]);
            }
            using var stream = new RawSourceWaveStream(new MemoryStream(input), WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
            var pcm = new ExclusivePcmProvider(stream.ToSampleProvider(), 24, format => format.Encoding == encoding);
            var output = Enumerable.Repeat((byte)0x5a, 56).ToArray();
            Equal(42, pcm.Read(output, 7, 42));
            var expected = Convert.FromHexString("000000000000FFFF3FFFFF3F0100C00100C0FFFF7FFFFF7F010080010080FFFF7FFFFF7F010080010080");
            if (!output.AsSpan(7, 42).SequenceEqual(expected))
                throw new Exception("24-bit PCM changed the expected signed stereo samples.");
            if (output.Take(7).Concat(output.Skip(49)).Any(value => value != 0x5a))
                throw new Exception("24-bit PCM wrote outside the requested buffer region.");
            Equal(0, pcm.Read(output, 7, 42));
        }
    }

    private static void Resampling()
    {
        foreach (var inputRate in new[] { 44100, 48000, 96000 })
        {
            var provider = new LiveAudioProvider(inputRate, 48000);
            var output = new float[960];
            long phase = 0;
            var crossings = 0;
            var previous = 0f;
            provider.Push(new float[inputRate * 5 / 1000]);
            for (var block = 0; block < 100; block++)
            {
                var mono = new float[inputRate / 100];
                for (var i = 0; i < mono.Length; i++)
                    mono[i] = (float)(0.25 * Math.Sin(phase++ * 2 * Math.PI * 1000 / inputRate));
                provider.Push(mono);
                provider.Read(output, 0, output.Length);
                for (var i = 0; i < output.Length; i += 2)
                {
                    if (previous <= 0 && output[i] > 0)
                        crossings++;
                    previous = output[i];
                }
            }
            if (crossings < 940 || crossings > 1010)
                throw new Exception($"Wrong tone frequency at {inputRate} Hz: {crossings} crossings.");
        }
    }

    private static void BufferPriming()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        var output = new float[960];
        provider.Push(Enumerable.Repeat(0.2f, 600).ToArray());
        provider.Read(output, 0, output.Length);
        if (output.Any(sample => sample != 0) || provider.ReadMetrics().MissingFrames != 0)
            throw new Exception("Priming consumed an incomplete block.");
        provider.Push(Enumerable.Repeat(0.2f, 120).ToArray());
        provider.Read(output, 0, output.Length);
        if (output.Max() < 0.1f || provider.ReadMetrics().BufferedMilliseconds > 6)
            throw new Exception("Audio did not start with the short reserve.");
    }

    private static void ClockDrift()
    {
        foreach (var drift in new[] { -0.001, 0, 0.001 })
        {
            var provider = new LiveAudioProvider(48000, 48000);
            provider.Push(new float[240]);
            var output = new float[960];
            var source = new float[481];
            var previousTotal = 0;
            for (var block = 1; block <= 6000; block++)
            {
                var total = (int)Math.Floor(block * 480 * (1 + drift));
                provider.Push(source.AsSpan(0, total - previousTotal));
                provider.Read(output, 0, output.Length);
                previousTotal = total;
            }
            var metrics = provider.ReadMetrics();
            if (metrics.MissingFrames != 0 || metrics.DroppedFrames != 0 || metrics.BufferedMilliseconds > 15)
                throw new Exception($"Clock drift {drift} grew latency or lost audio: {metrics}.");
        }
    }

    private static void RenderStall()
    {
        var provider = new LiveAudioProvider(48000, 48000);
        var source = new float[480];
        var output = new float[960];
        provider.Push(new float[240]);
        provider.Push(source);
        provider.Read(output, 0, output.Length);
        for (var packet = 0; packet < 20; packet++)
            provider.Push(source);
        provider.Read(output, 0, output.Length);
        var metrics = provider.ReadMetrics();
        if (metrics.DroppedFrames == 0 || metrics.MissingFrames != 0 || metrics.BufferedMilliseconds > 6)
            throw new Exception("Stale audio remained queued after a render stall: " + metrics);
    }

    private static void CableValidation()
    {
        var render = new EndpointInfo("render", "CABLE Input", 2, 48000, 32);
        var capture = new EndpointInfo("capture", "CABLE Output", 2, 48000, 32);
        DeviceCatalog.ValidateCable(new CableChoice(render, capture));
        Throws<InvalidOperationException>(() => DeviceCatalog.ValidateCable(new CableChoice(render, capture with { Channels = 10 })));
        Throws<InvalidOperationException>(() => DeviceCatalog.ValidateCable(new CableChoice(render, null)));
    }

    private static void FailedStartCleanup()
    {
        var input = new InputChoice(InputBackend.Wasapi, "not-a-device", "Test", 10, 48000, 32);
        var cable = new CableChoice(new EndpointInfo("none", "CABLE Input", 2, 48000, 32), null);
        using var bridge = new AudioBridge(input, cable, 0, 48000);
        Throws<InvalidOperationException>(() => bridge.Start(0));
        bridge.Dispose();
        bridge.Dispose();
    }

    private static void Benchmark(string path, int outputBits)
    {
        var rate = outputBits == 24 ? 44100 : 48000;
        const int channels = 10;
        var frames = rate / 100;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var decoder = new PcmChannelDecoder(format);
        var raw = new byte[frames * format.BlockAlign];
        for (var frame = 0; frame < frames; frame++)
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(frame * format.BlockAlign + 7 * 4),
                (float)(0.2 * Math.Sin(frame * 2 * Math.PI * 1000 / rate)));
        var mono = new float[frames];
        var provider = new LiveAudioProvider(rate, rate);
        IWaveProvider waveProvider = outputBits != 0
            ? new ExclusivePcmProvider(provider, outputBits, format => format.Encoding == WaveFormatEncoding.Extensible)
            : new SampleToWaveProvider(provider);
        var output = new byte[frames * waveProvider.WaveFormat.BlockAlign];
        provider.Push(new float[rate * 5 / 1000]);
        for (var i = 0; i < 100; i++)
        {
            decoder.Decode(raw, 7, mono);
            provider.Push(mono);
            waveProvider.Read(output, 0, output.Length);
        }
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var tick = new BenchmarkTick(10);
        WaitHandle[] waits = [tick, done.Token.WaitHandle];
        var processedBlocks = 0;
        while (WaitHandle.WaitAny(waits) == 0)
        {
            // Catch up coalesced timer ticks so the CPU workload remains 48,000 frames/second.
            var dueBlocks = (int)(timer.Elapsed.TotalMilliseconds / 10);
            while (processedBlocks < dueBlocks)
            {
                decoder.Decode(raw, 7, mono);
                provider.Push(mono);
                waveProvider.Read(output, 0, output.Length);
                processedBlocks++;
            }
        }
        timer.Stop();
        process.Refresh();
        var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var report = new
        {
            Workload = $"Synthetic 10-channel float32 capture, selected channel 8, {rate} Hz, stereo output, 5 ms reserve, consecutive capture/render blocks with a 10 ms clock",
            OutputEncoding = waveProvider.WaveFormat.Encoding.ToString(),
            OutputBits = waveProvider.WaveFormat.BitsPerSample,
            DurationMs = timer.Elapsed.TotalMilliseconds,
            ProcessCpuMs = cpuMs,
            PercentOfOneLogicalCore = cpuMs / timer.Elapsed.TotalMilliseconds * 100,
            LogicalProcessors = Environment.ProcessorCount,
            AllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart,
            InputFramesPerSecond = processedBlocks * frames / timer.Elapsed.TotalSeconds,
            Metrics = provider.ReadMetrics(),
            Boundary = "Managed pipeline and scheduling only; no Apollo, WASAPI/ASIO driver, VB-CABLE or FiveM measurement."
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"BENCHMARK: {cpuMs:0.0} ms CPU / {timer.Elapsed.TotalSeconds:0.0} s elapsed.");
    }

    private static void SettingsPersistence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ApolloMicBridge.Tests-" + Guid.NewGuid());
        var path = Path.Combine(folder, "settings.json");
        try
        {
            Equal(new UserSettings(InputBackend.Wasapi, null, null), SettingsStore.Load(path));
            var saved = new UserSettings(InputBackend.Asio, "Apollo ASIO", "cable-id", 9, -6, 96000, 5, true, 24);
            SettingsStore.Save(path, saved);
            Equal(saved, SettingsStore.Load(path));
            // A 0.1.0 settings file must retain the working shared output mode after an upgrade.
            File.WriteAllText(path, "{\"Backend\":1,\"InputId\":\"Apollo ASIO\",\"CableId\":\"cable-id\",\"Channel\":9,\"Gain\":-6,\"AsioRate\":96000,\"ReserveMilliseconds\":5}");
            Equal(saved with { ExclusiveOutput = false, OutputBits = 16 }, SettingsStore.Load(path));
            File.WriteAllText(path, "{\"Backend\":1,\"InputId\":\"Apollo ASIO\",\"CableId\":\"cable-id\",\"Channel\":9,\"Gain\":-6,\"AsioRate\":96000,\"ReserveMilliseconds\":5,\"ExclusiveOutput\":true}");
            Equal(saved with { OutputBits = 16 }, SettingsStore.Load(path));
            var changed = saved with { Gain = -12 };
            SettingsStore.Save(path, changed);
            Equal(changed, SettingsStore.Load(path));
            if (File.Exists(path + ".tmp"))
                throw new Exception("Incomplete settings transaction.");
            SettingsStore.Save(path, saved with { Channel = -1 });
            Throws<InvalidDataException>(() => SettingsStore.Load(path));
            SettingsStore.Save(path, saved with { Gain = 99 });
            Throws<InvalidDataException>(() => SettingsStore.Load(path));
            SettingsStore.Save(path, saved with { ReserveMilliseconds = 40 });
            Throws<InvalidDataException>(() => SettingsStore.Load(path));
            SettingsStore.Save(path, saved with { OutputBits = 32 });
            Throws<InvalidDataException>(() => SettingsStore.Load(path));
            File.WriteAllText(path, "not json");
            Throws<JsonException>(() => SettingsStore.Load(path));
        }
        finally
        {
            // Delete only the two files owned by this test; preserve anything else.
            File.Delete(path);
            File.Delete(path + ".tmp");
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
    }

    private static void StartupCommand()
    {
        Equal("\"C:\\Apps with spaces\\ApolloMicBridge.exe\" --autostart --background",
            StartupRegistration.CommandFor(@"C:\Apps with spaces\ApolloMicBridge.exe"));
        Throws<ArgumentException>(() => StartupRegistration.CommandFor("relative.exe"));
        Throws<ArgumentException>(() => StartupRegistration.CommandFor("C:\\app\" --something.exe"));
    }

    private static void Equal<T>(T expected, T actual) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Near(float expected, float actual)
    {
        if (Math.Abs(expected - actual) > 0.000001f)
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected exception: " + typeof(T).Name);
    }
}

// Benchmark only: ordinary .NET timers can round 10 ms ticks to 15.6 ms on Windows.
// A sleeping high-resolution timer preserves a representative 48 kHz CPU workload.
internal sealed class BenchmarkTick : WaitHandle
{
    internal BenchmarkTick(int firstTickMilliseconds)
    {
        SafeWaitHandle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x001F0003);
        if (SafeWaitHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        long dueTime = -firstTickMilliseconds * 10000L;
        if (!SetWaitableTimer(SafeWaitHandle, ref dueTime, 10, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
