using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ApolloMicBridge;

internal sealed class ExclusivePcmProvider : IWaveProvider
{
    private readonly ISampleProvider source;
    private readonly SampleToWaveProvider16? pcm16;
    private float[] samples = [];

    public WaveFormat WaveFormat { get; }

    public ExclusivePcmProvider(ISampleProvider source, int bits, Func<WaveFormat, bool> isSupported)
    {
        if (bits is not (16 or 24))
            throw new ArgumentOutOfRangeException(nameof(bits), "Exclusive output must use 16-bit or 24-bit PCM.");
        this.source = source;
        // Negotiate both representations of the same PCM bytes before opening WASAPI.
        WaveFormat = new WaveFormat(source.WaveFormat.SampleRate, bits, source.WaveFormat.Channels);
        if (!isSupported(WaveFormat))
        {
            WaveFormat = new WaveFormatExtensible(WaveFormat.SampleRate, bits, WaveFormat.Channels);
            if (!isSupported(WaveFormat))
                throw new NotSupportedException(Texts.Get("ExclusiveFormatUnsupported", WaveFormat.SampleRate, bits));
        }
        if (bits == 16)
            pcm16 = new SampleToWaveProvider16(source);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (pcm16 is not null)
            return pcm16.Read(buffer, offset, count);
        var samplesRequired = count / 3;
        if (samples.Length < samplesRequired)
            samples = new float[samplesRequired];
        var samplesRead = source.Read(samples, 0, samplesRequired);
        var bytes = buffer.AsSpan(offset, samplesRead * 3);
        for (var i = 0; i < samplesRead; i++)
        {
            var value = (int)(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
            bytes[i * 3] = (byte)value;
            bytes[i * 3 + 1] = (byte)(value >> 8);
            bytes[i * 3 + 2] = (byte)(value >> 16);
        }
        return bytes.Length;
    }
}
