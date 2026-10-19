using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Chistara.Services;

public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly object _captureGate = new();
    private readonly AudioAnalyzer _analyzer = new();
    private WasapiLoopbackCapture? _outputCapture;
    private Deck? _current;
    private CancellationTokenSource? _fadeCancellation;
    private float _volume = 0.72f;
    private bool _equalizerEnabled;
    private float _bass;
    private float _mid;
    private float _treble;
    private bool _reverbEnabled;
    private float _reverbMix = 0.18f;
    private bool _disposed;

    public event EventHandler? PlaybackEnded;
    public event EventHandler<Exception>? PlaybackError;

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return _current?.Output.PlaybackState == PlaybackState.Playing;
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                try { return _current?.Reader.CurrentTime ?? TimeSpan.Zero; }
                catch { return TimeSpan.Zero; }
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                try { return _current?.Reader.TotalTime ?? TimeSpan.Zero; }
                catch { return TimeSpan.Zero; }
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_gate)
            {
                if (_current is not null) _current.Volume.Volume = _volume;
            }
        }
    }

    public VisualizationFrame GetVisualizationFrame() => _analyzer.GetFrame();

    /// <summary>
    /// Captures the active Windows output mix only while WPF MediaElement is being used
    /// as a radio fallback. Samples feed the in-memory visualizer and are never stored.
    /// </summary>
    public bool StartOutputCaptureAnalysis()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopOutputCaptureAnalysis();
        _analyzer.Reset();
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OutputCapture_DataAvailable;
            lock (_captureGate) _outputCapture = capture;
            capture.StartRecording();
            return true;
        }
        catch
        {
            lock (_captureGate)
            {
                if (ReferenceEquals(_outputCapture, capture)) _outputCapture = null;
            }
            if (capture is not null)
            {
                capture.DataAvailable -= OutputCapture_DataAvailable;
                try { capture.Dispose(); } catch { }
            }
            return false;
        }
    }

    public void StopOutputCaptureAnalysis()
    {
        WasapiLoopbackCapture? capture;
        lock (_captureGate)
        {
            capture = _outputCapture;
            _outputCapture = null;
        }
        if (capture is null) return;
        capture.DataAvailable -= OutputCapture_DataAvailable;
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    public async Task PlayFileAsync(string path, TimeSpan crossfade, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The music file is unavailable.", path);
        StopOutputCaptureAnalysis();
        _analyzer.Reset();

        var deck = await CreateDeckAsync(path, networkStream: false, token);
        Deck? previous;
        CancellationTokenSource? fade;
        lock (_gate)
        {
            _fadeCancellation?.Cancel();
            _fadeCancellation?.Dispose();
            _fadeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            fade = _fadeCancellation;
            previous = _current;
            _current = deck;
            ApplyEffects(deck);
            deck.Volume.Volume = previous is not null && crossfade > TimeSpan.Zero ? 0 : _volume;
            deck.Output.Play();
        }

        if (previous is not null)
        {
            if (crossfade > TimeSpan.Zero)
                _ = CrossfadeAsync(previous, deck, crossfade, fade.Token);
            else
                DisposeDeck(previous);
        }
    }

    private void OutputCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is WasapiLoopbackCapture capture && e.BytesRecorded > 0)
            _analyzer.Push(e.Buffer, e.BytesRecorded, capture.WaveFormat);
    }

    public async Task PlayStreamAsync(string url, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The radio stream address is invalid.", nameof(url));
        StopOutputCaptureAnalysis();
        _analyzer.Reset();

        var deck = await CreateDeckAsync(uri.AbsoluteUri, networkStream: true, token);
        Deck? previous;
        lock (_gate)
        {
            _fadeCancellation?.Cancel();
            _fadeCancellation?.Dispose();
            _fadeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            previous = _current;
            _current = deck;
            ApplyEffects(deck);
            deck.Volume.Volume = _volume;
            deck.Output.Play();
        }
        if (previous is not null) DisposeDeck(previous);
    }

    public void Pause()
    {
        lock (_gate) _current?.Output.Pause();
    }

    public void Resume()
    {
        lock (_gate) _current?.Output.Play();
    }

    public void Stop()
    {
        StopOutputCaptureAnalysis();
        Deck? deck;
        lock (_gate)
        {
            _fadeCancellation?.Cancel();
            _fadeCancellation?.Dispose();
            _fadeCancellation = null;
            deck = _current;
            _current = null;
        }
        if (deck is not null) DisposeDeck(deck);
        _analyzer.Reset();
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_current is null) return;
            try
            {
                var bounded = position < TimeSpan.Zero ? TimeSpan.Zero : position > _current.Reader.TotalTime ? _current.Reader.TotalTime : position;
                _current.Reader.CurrentTime = bounded;
            }
            catch { }
        }
    }

    public void ConfigureEqualizer(bool enabled, float bass, float mid, float treble)
    {
        _equalizerEnabled = enabled;
        _bass = Math.Clamp(bass, -12, 12);
        _mid = Math.Clamp(mid, -12, 12);
        _treble = Math.Clamp(treble, -12, 12);
        lock (_gate) _current?.Equalizer.Configure(_equalizerEnabled, _bass, _mid, _treble);
    }

    public void ConfigureReverb(bool enabled, float mix)
    {
        _reverbEnabled = enabled;
        _reverbMix = Math.Clamp(mix, 0, 0.6f);
        lock (_gate) _current?.Reverb.Configure(_reverbEnabled, _reverbMix);
    }

    private async Task<Deck> CreateDeckAsync(string path, bool networkStream, CancellationToken token)
    {
        var creation = Task.Run(() => CreateDeck(path, networkStream));
        try
        {
            return await creation.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = creation.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) DisposeDeck(task.Result);
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private Deck CreateDeck(string path, bool networkStream)
    {
        WaveStream reader;
        var extension = Path.GetExtension(path);
        if (!networkStream && (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aif", StringComparison.OrdinalIgnoreCase)))
        {
            reader = new AudioFileReader(path);
        }
        else
        {
            reader = new MediaFoundationReader(path);
        }

        WaveOutEvent? output = null;
        try
        {
            ISampleProvider source = reader is ISampleProvider sampleProvider ? sampleProvider : reader.ToSampleProvider();
            var equalizer = new ThreeBandEqualizer(source);
            var reverb = new SimpleReverbProvider(equalizer);
            var volume = new VolumeSampleProvider(reverb) { Volume = _volume };
            var analysis = new AnalyzerSampleProvider(volume, _analyzer);
            output = new WaveOutEvent { DesiredLatency = 140, NumberOfBuffers = 3 };
            var deck = new Deck(reader, output, equalizer, reverb, volume);
            output.Init(new SampleToWaveProvider16(analysis));
            output.PlaybackStopped += (_, args) => OnDeckStopped(deck, args.Exception);
            return deck;
        }
        catch
        {
            try { output?.Dispose(); } catch { }
            reader.Dispose();
            throw;
        }
    }

    private void ApplyEffects(Deck deck)
    {
        deck.Equalizer.Configure(_equalizerEnabled, _bass, _mid, _treble);
        deck.Reverb.Configure(_reverbEnabled, _reverbMix);
        deck.Volume.Volume = _volume;
    }

    private async Task CrossfadeAsync(Deck oldDeck, Deck newDeck, TimeSpan duration, CancellationToken token)
    {
        try
        {
            var steps = Math.Clamp((int)(duration.TotalMilliseconds / 40), 8, 300);
            var delay = TimeSpan.FromMilliseconds(duration.TotalMilliseconds / steps);
            for (var step = 1; step <= steps; step++)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                var fraction = (float)step / steps;
                oldDeck.Volume.Volume = Math.Clamp(_volume * (1 - fraction), 0, 1);
                newDeck.Volume.Volume = Math.Clamp(_volume * fraction, 0, 1);
            }
            DisposeDeck(oldDeck);
        }
        catch (OperationCanceledException)
        {
            DisposeDeck(oldDeck);
            newDeck.Volume.Volume = _volume;
        }
        catch
        {
            DisposeDeck(oldDeck);
            newDeck.Volume.Volume = _volume;
        }
    }

    private void OnDeckStopped(Deck deck, Exception? exception)
    {
        bool isCurrent;
        bool endedNaturally;
        lock (_gate)
        {
            isCurrent = ReferenceEquals(deck, _current);
            endedNaturally = isCurrent && !deck.ManualStop && deck.Output.PlaybackState == PlaybackState.Stopped;
        }
        if (!isCurrent) return;
        if (exception is not null)
        {
            PlaybackError?.Invoke(this, exception);
            return;
        }
        if (endedNaturally) PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private static void DisposeDeck(Deck deck)
    {
        if (deck.ManualStop) return;
        deck.ManualStop = true;
        try { deck.Output.Stop(); } catch { }
        try { deck.Output.Dispose(); } catch { }
        try { deck.Reader.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class AnalyzerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioAnalyzer _analyzer;

        public AnalyzerSampleProvider(ISampleProvider source, AudioAnalyzer analyzer)
        {
            _source = source;
            _analyzer = analyzer;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read > 0)
                _analyzer.Push(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate);
            return read;
        }
    }

    private sealed class Deck
    {
        public Deck(WaveStream reader, WaveOutEvent output, ThreeBandEqualizer equalizer, SimpleReverbProvider reverb, VolumeSampleProvider volume)
        {
            Reader = reader;
            Output = output;
            Equalizer = equalizer;
            Reverb = reverb;
            Volume = volume;
        }

        public WaveStream Reader { get; }
        public WaveOutEvent Output { get; }
        public ThreeBandEqualizer Equalizer { get; }
        public SimpleReverbProvider Reverb { get; }
        public VolumeSampleProvider Volume { get; }
        public bool ManualStop { get; set; }
    }

    private sealed class ThreeBandEqualizer : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly object _filterGate = new();
        private BiQuadFilter[,] _filters;
        private bool _enabled;
        private float _bass;
        private float _mid;
        private float _treble;

        public ThreeBandEqualizer(ISampleProvider source)
        {
            _source = source;
            _filters = new BiQuadFilter[Math.Max(1, source.WaveFormat.Channels), 3];
            RebuildFilters();
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public void Configure(bool enabled, float bass, float mid, float treble)
        {
            lock (_filterGate)
            {
                _enabled = enabled;
                _bass = bass;
                _mid = mid;
                _treble = treble;
                RebuildFilters();
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (!_enabled) return read;
            lock (_filterGate)
            {
                var channels = WaveFormat.Channels;
                for (var index = 0; index < read; index++)
                {
                    var channel = index % channels;
                    var sample = buffer[offset + index];
                    sample = _filters[channel, 0].Transform(sample);
                    sample = _filters[channel, 1].Transform(sample);
                    sample = _filters[channel, 2].Transform(sample);
                    buffer[offset + index] = Math.Clamp(sample, -1f, 1f);
                }
            }
            return read;
        }

        private void RebuildFilters()
        {
            var channels = Math.Max(1, WaveFormat.Channels);
            _filters = new BiQuadFilter[channels, 3];
            for (var channel = 0; channel < channels; channel++)
            {
                _filters[channel, 0] = BiQuadFilter.LowShelf(WaveFormat.SampleRate, 180, 0.72f, _bass);
                _filters[channel, 1] = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, 1100, 0.9f, _mid);
                _filters[channel, 2] = BiQuadFilter.HighShelf(WaveFormat.SampleRate, 6200, 0.72f, _treble);
            }
        }
    }

    private sealed class SimpleReverbProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _delayBuffer;
        private int _delayIndex;
        private bool _enabled;
        private float _mix = 0.18f;

        public SimpleReverbProvider(ISampleProvider source)
        {
            _source = source;
            var delayFrames = Math.Max(1, (int)(source.WaveFormat.SampleRate * 0.073));
            _delayBuffer = new float[delayFrames * Math.Max(1, source.WaveFormat.Channels)];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public void Configure(bool enabled, float mix)
        {
            _enabled = enabled;
            _mix = Math.Clamp(mix, 0, 0.6f);
            if (!enabled) Array.Clear(_delayBuffer);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (!_enabled || _mix <= 0) return read;
            var wet = _mix;
            const float feedback = 0.32f;
            for (var index = 0; index < read; index++)
            {
                var input = buffer[offset + index];
                var delayed = _delayBuffer[_delayIndex];
                _delayBuffer[_delayIndex] = Math.Clamp(input + delayed * feedback, -1f, 1f);
                buffer[offset + index] = Math.Clamp(input * (1 - wet) + delayed * wet, -1f, 1f);
                _delayIndex++;
                if (_delayIndex >= _delayBuffer.Length) _delayIndex = 0;
            }
            return read;
        }
    }
}
