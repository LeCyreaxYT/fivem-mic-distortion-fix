using System.Buffers.Binary;
using NAudio.Dsp;
using NAudio.Wave;

namespace ApolloMicBridge;

// Uses the physical frame stride, including channels that are not selected.
internal sealed class PcmChannelDecoder
{
    private readonly WaveFormat format;
    private readonly bool floatingPoint;
    private readonly int sampleBytes;
    private readonly int frameBytes;

    public PcmChannelDecoder(WaveFormat format)
    {
        this.format = format is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : format;
        frameBytes = format.BlockAlign;
        floatingPoint = this.format.Encoding == WaveFormatEncoding.IeeeFloat;
        sampleBytes = this.format.BitsPerSample / 8;
        if ((!floatingPoint && this.format.Encoding != WaveFormatEncoding.Pcm)
            || (floatingPoint && this.format.BitsPerSample is not (32 or 64))
            || (!floatingPoint && this.format.BitsPerSample is not (8 or 16 or 24 or 32))
            || this.format.Channels < 1 || frameBytes < sampleBytes * this.format.Channels)
            throw new NotSupportedException("Unsupported capture format: " + format);
    }

    public int Decode(ReadOnlySpan<byte> data, int channel, Span<float> output)
    {
        if (channel < 0 || channel >= format.Channels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (data.Length % frameBytes != 0)
            throw new ArgumentException("Capture packet ends inside an audio frame.", nameof(data));
        var frames = data.Length / frameBytes;
        if (output.Length < frames)
            throw new ArgumentException("Mono buffer is too small.", nameof(output));
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = data.Slice(frame * frameBytes + channel * sampleBytes, sampleBytes);
            output[frame] = floatingPoint
                ? (sampleBytes == 4 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                    : (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample)))
                : sampleBytes switch
                {
                    1 => (sample[0] - 128) / 128f,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
                    4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                    _ => throw new InvalidOperationException("Unsupported PCM container size.")
                };
        }
        return frames;
    }
}

internal sealed record AudioMetrics(float Peak, int BufferedMilliseconds, long CapturedFrames,
    long MissingFrames, long DroppedFrames, long ClippedSamples, long InvalidSamples);

internal sealed class LiveAudioProvider : ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] queue;
    private readonly int inputRate;
    private readonly int targetFrames;
    private readonly WdlResampler resampler = new();
    private float[] monoOutput = [];
    private int readIndex;
    private int queued;
    private int largestPacketFrames;
    private float gain = 1;
    private float peak;
    private long captured;
    private long missing;
    private long dropped;
    private long clipped;
    private long invalid;
    private bool primed;
    private double clockCorrection;

    public WaveFormat WaveFormat { get; }

    public LiveAudioProvider(int inputRate, int outputRate, int reserveMilliseconds = 5)
    {
        if (inputRate < 8000 || outputRate < 8000)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (reserveMilliseconds is not (5 or 10 or 20))
            throw new ArgumentOutOfRangeException(nameof(reserveMilliseconds));
        this.inputRate = inputRate;
        targetFrames = inputRate * reserveMilliseconds / 1000;
        queue = new float[inputRate / 3];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, 2);
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(false);
        resampler.SetRates(inputRate, outputRate);
    }

    public void SetGain(float decibels)
    {
        if (!float.IsFinite(decibels) || decibels is < -24 or > 12)
            throw new ArgumentOutOfRangeException(nameof(decibels));
        Volatile.Write(ref gain, MathF.Pow(10, decibels / 20));
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        var currentGain = Volatile.Read(ref gain);
        lock (gate)
        {
            captured += samples.Length;
            largestPacketFrames = Math.Max(largestPacketFrames, samples.Length);
            var writeIndex = (readIndex + queued) % queue.Length;
            foreach (var input in samples)
            {
                var value = input * currentGain;
                if (!float.IsFinite(value))
                {
                    value = 0;
                    invalid++;
                }
                peak = Math.Max(peak, Math.Abs(value));
                if (Math.Abs(value) > 1)
                {
                    clipped++;
                    value = Math.Clamp(value, -1, 1);
                }
                if (queued == queue.Length)
                {
                    if (++readIndex == queue.Length)
                        readIndex = 0;
                    queued--;
                    dropped++;
                }
                queue[writeIndex] = value;
                if (++writeIndex == queue.Length)
                    writeIndex = 0;
                queued++;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count % 2 != 0)
            throw new ArgumentException("Stereo reads must contain complete frames.", nameof(count));
        var frames = count / 2;
        if (monoOutput.Length < frames)
            monoOutput = new float[frames];
        var blockInputFrames = frames * (double)inputRate / WaveFormat.SampleRate;
        var wholeBlockFrames = (int)Math.Ceiling(blockInputFrames);
        int available;
        lock (gate)
        {
            // A stalled render thread must not leave old audio queued for seconds afterward.
            // Allow one capture packet plus a render block; large ASIO packets need that space.
            var maximumQueued = wholeBlockFrames + targetFrames
                + Math.Max(largestPacketFrames, wholeBlockFrames) + inputRate * 5 / 1000;
            if (primed && queued > maximumQueued)
            {
                var stale = queued - wholeBlockFrames - targetFrames;
                readIndex = (readIndex + stale) % queue.Length;
                queued -= stale;
                dropped += stale;
                clockCorrection = 0;
            }
            available = queued;
        }
        if (!primed && available < wholeBlockFrames + targetFrames)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        primed = true;
        // Independent device clocks drift. Keep a short reserve without growing latency forever.
        // Compare the reserve after this block; smooth the correction to avoid pitch modulation.
        var correction = Math.Clamp((available - blockInputFrames - targetFrames) / inputRate * 0.5, -0.002, 0.002);
        clockCorrection += (correction - clockCorrection) * 0.05;
        resampler.SetRates(inputRate * (1 + clockCorrection), WaveFormat.SampleRate);
        var needed = resampler.ResamplePrepare(frames, 1, out var input, out var inputOffset);
        var supplied = ReadMono(input.AsSpan(inputOffset, needed));
        var rendered = resampler.ResampleOut(monoOutput, 0, supplied, frames, 1);
        for (var i = 0; i < frames; i++)
        {
            var value = i < rendered ? Math.Clamp(monoOutput[i], -1, 1) : 0;
            buffer[offset + i * 2] = value;
            buffer[offset + i * 2 + 1] = value;
        }
        return count;
    }

    internal int ReadMono(Span<float> destination)
    {
        lock (gate)
        {
            var available = Math.Min(queued, destination.Length);
            var first = Math.Min(available, queue.Length - readIndex);
            queue.AsSpan(readIndex, first).CopyTo(destination);
            queue.AsSpan(0, available - first).CopyTo(destination[first..]);
            readIndex = (readIndex + available) % queue.Length;
            queued -= available;
            destination[available..].Clear();
            if (captured != 0)
                missing += destination.Length - available;
            if (available < destination.Length)
                primed = false;
            return destination.Length;
        }
    }

    public AudioMetrics ReadMetrics()
    {
        lock (gate)
        {
            var result = new AudioMetrics(peak, queued * 1000 / inputRate, captured, missing, dropped, clipped, invalid);
            peak = 0;
            return result;
        }
    }
}
