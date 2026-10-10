using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ApolloMicBridge;

internal sealed class AudioBridge : IDisposable
{
    private const int CaptureBufferMilliseconds = 10;
    private const int OutputBufferMilliseconds = 10;
    private readonly InputChoice input;
    private readonly CableChoice cable;
    private readonly int channel;
    private readonly int asioSampleRate;
    private readonly int reserveMilliseconds;
    private readonly bool exclusiveOutput;
    private readonly int outputBits;
    private MMDevice? captureDevice;
    private MMDevice? renderDevice;
    private WasapiCapture? capture;
    private AsioOut? asio;
    private WasapiOut? output;
    private PcmChannelDecoder? decoder;
    private float[] mono = [];
    private volatile bool stopping;
    private int faultReported;

    public LiveAudioProvider? Provider { get; private set; }
    public int AsioBufferSamples { get; private set; }
    public event Action<Exception>? Failed;

    public AudioBridge(InputChoice input, CableChoice cable, int channel, int asioSampleRate,
        int reserveMilliseconds = 5, bool exclusiveOutput = false, int outputBits = 16)
    {
        this.input = input;
        this.cable = cable;
        this.channel = channel;
        this.asioSampleRate = asioSampleRate;
        this.reserveMilliseconds = reserveMilliseconds;
        this.exclusiveOutput = exclusiveOutput;
        this.outputBits = outputBits;
    }

    public void Start(float gainDecibels)
    {
        DeviceCatalog.ValidateCable(cable);
        if (input.Id == cable.Capture!.Id)
            throw new InvalidOperationException(Texts.Get("FeedbackInput"));
        using var enumerator = new MMDeviceEnumerator();
        renderDevice = enumerator.GetDevice(cable.Render.Id);
        using var formatProbe = renderDevice.AudioClient;
        var outputRate = formatProbe.MixFormat.SampleRate;
        if (input.Backend == InputBackend.Wasapi)
        {
            captureDevice = enumerator.GetDevice(input.Id);
            capture = new WasapiCapture(captureDevice, true, CaptureBufferMilliseconds);
            if (channel < 0 || channel >= capture.WaveFormat.Channels)
                throw new InvalidOperationException(Texts.Get("ChannelUnavailable"));
            decoder = new PcmChannelDecoder(capture.WaveFormat);
            Provider = new LiveAudioProvider(capture.WaveFormat.SampleRate, outputRate, reserveMilliseconds);
            capture.DataAvailable += CaptureData;
            capture.RecordingStopped += SourceStopped;
        }
        else
        {
            asio = new AsioOut(input.Id);
            if (channel < 0 || channel >= asio.DriverInputChannelCount)
                throw new InvalidOperationException(Texts.Get("ChannelUnavailable"));
            asio.InputChannelOffset = channel;
            asio.InitRecordAndPlayback(null, 1, asioSampleRate);
            mono = new float[asio.FramesPerBuffer];
            AsioBufferSamples = asio.FramesPerBuffer;
            Provider = new LiveAudioProvider(asioSampleRate, outputRate, reserveMilliseconds);
            asio.AudioAvailable += AsioData;
            asio.PlaybackStopped += SourceStopped;
            asio.DriverResetRequest += DriverReset;
        }
        Provider.SetGain(gainDecibels);
        var outputMode = exclusiveOutput ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        // Probe both PCM descriptors before initialization, without an extra resampler.
        IWaveProvider waveProvider = exclusiveOutput
            ? new ExclusivePcmProvider(Provider, outputBits, format => formatProbe.IsFormatSupported(outputMode, format))
            : new SampleToWaveProvider(Provider);
        output = new WasapiOut(renderDevice, outputMode, true, OutputBufferMilliseconds);
        output.PlaybackStopped += SourceStopped;
        output.Init(waveProvider);
        output.Play();
        if (capture is not null)
            capture.StartRecording();
        else
            asio!.Play();
    }

    private void CaptureData(object? sender, WaveInEventArgs args)
    {
        if (stopping || args.BytesRecorded == 0)
            return;
        try
        {
            var frames = args.BytesRecorded / capture!.WaveFormat.BlockAlign;
            if (mono.Length < frames)
                mono = new float[frames];
            var decoded = decoder!.Decode(args.Buffer.AsSpan(0, args.BytesRecorded), channel, mono);
            Provider!.Push(mono.AsSpan(0, decoded));
        }
        catch (Exception error)
        {
            ReportFault(error);
        }
    }

    private void AsioData(object? sender, AsioAudioAvailableEventArgs args)
    {
        if (stopping)
            return;
        try
        {
            args.GetAsInterleavedSamples(mono);
            Provider!.Push(mono.AsSpan(0, args.SamplesPerBuffer));
        }
        catch (Exception error)
        {
            ReportFault(error);
        }
    }

    private void DriverReset(object? sender, EventArgs args)
    {
        ReportFault(new InvalidOperationException(Texts.Get("DriverReset")));
    }

    private void SourceStopped(object? sender, StoppedEventArgs args)
    {
        if (!stopping)
            ReportFault(args.Exception ?? new InvalidOperationException(Texts.Get("DeviceStopped")));
    }

    private void ReportFault(Exception error)
    {
        if (!stopping && Interlocked.Exchange(ref faultReported, 1) == 0)
        {
            Failed?.Invoke(error);
        }
    }

    public void Dispose()
    {
        if (stopping)
            return;
        stopping = true;
        // Dispose joins WASAPI threads before releasing their devices. ASIO stops before releasing its driver.
        List<Exception> errors = [];
        foreach (var owner in new IDisposable?[] { output, capture, asio, captureDevice, renderDevice })
        {
            try { owner?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0)
            throw new AggregateException("Audio device cleanup failed.", errors);
    }
}
