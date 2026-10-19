using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace Chistara.Services;

/// <summary>A single immutable snapshot of Chistara's real-time audio analysis.</summary>
public sealed class VisualizationFrame
{
    public VisualizationFrame(float[] spectrum, float[] waveform, float level, float bass, float beat)
    {
        Spectrum = spectrum;
        Waveform = waveform;
        Level = level;
        Bass = bass;
        Beat = beat;
    }

    public float[] Spectrum { get; }
    public float[] Waveform { get; }
    public float Level { get; }
    public float Bass { get; }
    public float Beat { get; }
}

/// <summary>
/// Lock-bounded PCM ring buffer with on-demand Hann-windowed FFT analysis. Audio is
/// retained only in memory for a fraction of a second and is never written to disk.
/// </summary>
internal sealed class AudioAnalyzer
{
    private const int RingSize = 8192;
    private const int RingMask = RingSize - 1;
    private const int FftSize = 2048;
    private static readonly float[] HannWindow = CreateHannWindow();
    private readonly object _gate = new();
    private readonly object _analysisGate = new();
    private readonly float[] _ring = new float[RingSize];
    private readonly float[] _analysisSamples = new float[FftSize];
    private readonly Complex[] _fftScratch = new Complex[FftSize];
    private int[] _bandLowBins = [];
    private int[] _bandHighBins = [];
    private int _bandMapSampleRate;
    private int _bandMapCount;
    private int _writeIndex;
    private int _sampleCount;
    private int _sampleRate = 44100;
    private long _lastWriteTimestamp;
    private float _averageBass;
    private float _beatEnvelope;

    public void Push(float[] buffer, int offset, int count, int channels, int sampleRate)
    {
        if (count <= 0 || channels <= 0) return;
        var frames = count / channels;
        if (frames <= 0) return;

        lock (_gate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var sampleOffset = offset + frame * channels;
                for (var channel = 0; channel < channels; channel++)
                    sum += buffer[sampleOffset + channel];
                WriteSample(Math.Clamp(sum / channels, -1f, 1f));
            }
            _lastWriteTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public void Push(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = bytesPerSample * channels;
        var frames = bytesRecorded / frameSize;
        if (frames <= 0) return;
        var floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                            (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        lock (_gate)
        {
            _sampleRate = Math.Max(8000, format.SampleRate);
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var frameOffset = frame * frameSize;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = frameOffset + channel * bytesPerSample;
                    sum += ReadSample(buffer, index, format.BitsPerSample, floatingPoint);
                }
                WriteSample(Math.Clamp(sum / channels, -1f, 1f));
            }
            _lastWriteTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public VisualizationFrame GetFrame(int bandCount = 64, int waveformPoints = 192)
    {
        bandCount = Math.Clamp(bandCount, 16, 128);
        waveformPoints = Math.Clamp(waveformPoints, 64, 512);
        lock (_analysisGate)
            return GetFrameCore(bandCount, waveformPoints);
    }

    private VisualizationFrame GetFrameCore(int bandCount, int waveformPoints)
    {
        var samples = _analysisSamples;
        Array.Clear(samples);
        int available;
        int sampleRate;
        long lastWrite;

        lock (_gate)
        {
            available = Math.Min(_sampleCount, FftSize);
            sampleRate = _sampleRate;
            lastWrite = _lastWriteTimestamp;
            var source = (_writeIndex - available + RingSize) & RingMask;
            var destination = FftSize - available;
            for (var index = 0; index < available; index++)
                samples[destination + index] = _ring[(source + index) & RingMask];
        }

        var age = lastWrite == 0
            ? double.PositiveInfinity
            : (Stopwatch.GetTimestamp() - lastWrite) / (double)Stopwatch.Frequency;
        var staleDecay = age <= 0.12 ? 1f : (float)Math.Exp(-(age - 0.12) * 4.5);
        if (age > 2.5 || available == 0) staleDecay = 0;

        var waveform = new float[waveformPoints];
        for (var point = 0; point < waveformPoints; point++)
        {
            var position = (int)Math.Round(point * (FftSize - 1d) / Math.Max(1, waveformPoints - 1));
            waveform[point] = Math.Clamp(samples[position] * staleDecay, -1f, 1f);
        }

        var energyStart = Math.Max(0, FftSize - Math.Min(1024, available));
        double sumSquares = 0;
        var energyCount = Math.Max(1, FftSize - energyStart);
        for (var index = energyStart; index < FftSize; index++)
            sumSquares += samples[index] * samples[index];
        var rms = (float)Math.Sqrt(sumSquares / energyCount);
        var level = Math.Clamp(rms * 4.6f * staleDecay, 0, 1);

        var fft = _fftScratch;
        for (var index = 0; index < FftSize; index++)
        {
            fft[index].X = samples[index] * HannWindow[index];
            fft[index].Y = 0;
        }
        // NAudio uses false for the forward transform (true produces the inverse transform).
        FastFourierTransform.FFT(false, 11, fft);

        var spectrum = new float[bandCount];
        EnsureBandMap(sampleRate, bandCount);
        for (var band = 0; band < bandCount; band++)
        {
            var lowBin = _bandLowBins[band];
            var highBin = _bandHighBins[band];
            var maximumMagnitude = 0d;
            for (var bin = lowBin; bin < highBin; bin++)
            {
                var magnitude = Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y) * 2 / FftSize;
                maximumMagnitude = Math.Max(maximumMagnitude, magnitude);
            }
            var decibels = 20 * Math.Log10(maximumMagnitude + 1e-8);
            var normalized = Math.Clamp((decibels + 72) / 68, 0, 1);
            spectrum[band] = (float)Math.Pow(normalized, 1.12) * staleDecay;
        }

        var bassBands = Math.Clamp((int)Math.Round(bandCount * 0.28), 3, bandCount);
        var bass = 0f;
        for (var index = 0; index < bassBands; index++) bass += spectrum[index];
        bass /= bassBands;
        float beat;
        lock (_gate)
        {
            if (_averageBass <= 0.0001f) _averageBass = bass;
            _averageBass = _averageBass * 0.94f + bass * 0.06f;
            var onset = Math.Clamp((bass - _averageBass * 1.05f) / (_averageBass * 0.72f + 0.035f), 0, 1);
            _beatEnvelope = Math.Max(onset, _beatEnvelope * 0.80f);
            beat = _beatEnvelope * staleDecay;
        }

        return new VisualizationFrame(spectrum, waveform, level, Math.Clamp(bass, 0, 1), Math.Clamp(beat, 0, 1));
    }

    private void EnsureBandMap(int sampleRate, int bandCount)
    {
        if (_bandMapSampleRate == sampleRate && _bandMapCount == bandCount) return;
        _bandLowBins = new int[bandCount];
        _bandHighBins = new int[bandCount];
        const double minimumFrequency = 35;
        var maximumFrequency = Math.Min(18000, sampleRate * 0.48);
        for (var band = 0; band < bandCount; band++)
        {
            var lowFrequency = minimumFrequency * Math.Pow(maximumFrequency / minimumFrequency, band / (double)bandCount);
            var highFrequency = minimumFrequency * Math.Pow(maximumFrequency / minimumFrequency, (band + 1d) / bandCount);
            var lowBin = Math.Clamp((int)Math.Floor(lowFrequency * FftSize / sampleRate), 1, FftSize / 2 - 1);
            _bandLowBins[band] = lowBin;
            _bandHighBins[band] = Math.Clamp((int)Math.Ceiling(highFrequency * FftSize / sampleRate), lowBin + 1, FftSize / 2);
        }
        _bandMapSampleRate = sampleRate;
        _bandMapCount = bandCount;
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[FftSize];
        for (var index = 0; index < FftSize; index++)
            window[index] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * index / (FftSize - 1))));
        return window;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _writeIndex = 0;
            _sampleCount = 0;
            _lastWriteTimestamp = 0;
            _averageBass = 0;
            _beatEnvelope = 0;
        }
    }

    private void WriteSample(float sample)
    {
        _ring[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) & RingMask;
        if (_sampleCount < RingSize) _sampleCount++;
    }

    private static float ReadSample(byte[] data, int index, int bitsPerSample, bool floatingPoint)
    {
        if (floatingPoint && bitsPerSample == 32)
            return BitConverter.ToSingle(data, index);

        return bitsPerSample switch
        {
            8 => (data[index] - 128) / 128f,
            16 => (short)(data[index] | data[index + 1] << 8) / 32768f,
            24 => Read24Bit(data, index) / 8388608f,
            32 => (int)(data[index] | data[index + 1] << 8 | data[index + 2] << 16 | data[index + 3] << 24) / 2147483648f,
            _ => 0
        };
    }

    private static int Read24Bit(byte[] data, int index)
    {
        var value = data[index] | data[index + 1] << 8 | data[index + 2] << 16;
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
        return value;
    }
}
