using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using XTONE.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

var firstOwner = new DisposalCounter();
var secondOwner = new DisposalCounter();
using (var pipeline = new GaplessSampleProvider(Segment("first", [1, 2, 3, 4], firstOwner)))
{
    AudioBoundary? boundary = null;
    pipeline.BoundaryReached += value => boundary = value;
    Check(pipeline.SetNext(pipeline.Sequence, Segment("second", [5, 6, 7, 8, 9, 10], secondOwner)), "next segment accepted");
    var output = new float[10];
    Check(pipeline.Read(output, 0, output.Length) == 10, "a boundary fills the same output block");
    Check(output.SequenceEqual(new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }), "gapless concatenation inserts no silence and loses no samples");
    Check(boundary?.Path == "second" && Math.Abs(boundary.StartSeconds - .2) < .000001, "rendered boundary marker is sample accurate");
    Check(firstOwner.Count == 1, "finished decoder is released exactly once");
    Check(pipeline.Read(output, 0, output.Length) == 0 && pipeline.EndOfStream, "end of queue is finite");
    var staleOwner = new DisposalCounter();
    Check(!pipeline.SetNext(0, Segment("stale", [11, 12], staleOwner)) && staleOwner.Count == 1, "stale preparations are rejected and disposed");
}
Check(firstOwner.Count == 1 && secondOwner.Count == 1, "pipeline disposal releases each decoder once");

var replaced = new DisposalCounter();
var replacement = new DisposalCounter();
using (var pipeline = new GaplessSampleProvider(Segment("first", [1, 2])))
{
    pipeline.SetNext(0, Segment("old next", [3, 4], replaced));
    pipeline.SetNext(0, Segment("new next", [5, 6], replacement));
    Check(replaced.Count == 1, "replacing a queued preparation closes the superseded file");
    pipeline.SetNext(0, null);
    Check(replacement.Count == 1, "clearing the next track closes its preparation");
}

var rejectedFormat = new DisposalCounter();
var retainedNext = new DisposalCounter();
using (var pipeline = new GaplessSampleProvider(Segment("first", [1, 2])))
{
    pipeline.SetNext(0, Segment("valid next", [3, 4], retainedNext));
    try
    {
        pipeline.SetNext(0, new AudioSegment("wrong rate", TimeSpan.FromSeconds(1), TimeSpan.Zero,
            new ArrayProvider([9, 10], 20), rejectedFormat));
        throw new InvalidOperationException("Mismatched queued audio must fail.");
    }
    catch (ArgumentException) { }
    Check(rejectedFormat.Count == 1, "a rejected output format releases its decoder ownership");
    var block = new float[4];
    Check(pipeline.Read(block, 0, 4) == 4 && block.SequenceEqual(new float[] { 1, 2, 3, 4 })
        && retainedNext.Count == 0, "a rejected preparation does not replace the valid queued track");
}
Check(retainedNext.Count == 1 && rejectedFormat.Count == 1, "rejected and accepted preparation owners are each disposed once");

var counted = new CountedProvider(new ArrayProvider([1, 2]));
using (var pipeline = new GaplessSampleProvider(new AudioSegment("finite", TimeSpan.FromSeconds(.1), TimeSpan.Zero, counted)))
{
    var output = new float[4];
    Check(pipeline.Read(output, 0, 0) == 0 && counted.Reads == 0, "a zero-sample output request does not consume decoded audio");
    pipeline.Read(output, 0, 4);
    var eofReads = counted.Reads;
    Check(pipeline.Read(output, 0, 4) == 0 && counted.Reads == eofReads, "an exhausted decoder is not read again after end of stream");
    try { pipeline.Read(output, 0, -1); throw new InvalidOperationException("Negative output reads must fail."); }
    catch (ArgumentOutOfRangeException) { }
    Check(counted.Reads == eofReads, "invalid output requests cannot advance the decoder");
}
using (var pipeline = new GaplessSampleProvider(Segment("alias", [1, 2, 3, 4])))
{
    var adapter = new SampleToWaveProvider(pipeline); var bytes = new byte[16];
    Check(adapter.Read(bytes, 0, bytes.Length) == bytes.Length && BitConverter.ToSingle(bytes, 12) == 4,
        "gapless read validation accepts NAudio's byte/float output buffer alias");
}

var prefetched = PrefetchedSampleProvider.Create(new ArrayProvider([1, 2, 3, 4, 5, 6, 7, 8]), 6);
var prebufferOutput = new float[8];
Check(prefetched.Read(prebufferOutput, 0, 4) == 4 && prefetched.Read(prebufferOutput, 4, 4) == 4
      && prebufferOutput.SequenceEqual(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "prebuffer hands off to the decoder without repeating samples");
var byteOutput = new byte[32];
var byteAdapter = new SampleToWaveProvider(PrefetchedSampleProvider.Create(new ArrayProvider([1, 2, 3, 4, 5, 6, 7, 8]), 6));
Check(byteAdapter.Read(byteOutput, 0, byteOutput.Length) == byteOutput.Length
    && BitConverter.ToSingle(byteOutput, 0) == 1 && BitConverter.ToSingle(byteOutput, 28) == 8,
    "prebuffer supports NAudio's byte/float output buffer alias");

var sine = Enumerable.Range(0, 4096).Select(index => (float)(.5 * Math.Sin(index / 2 * 2 * Math.PI * 997 / 48000))).ToArray();
var analyzer = new AudioTapProvider(new ArrayProvider(sine, 48000));
var tinyBuffer = new float[128];
while (analyzer.Read(tinyBuffer, 0, tinyBuffer.Length) != 0) { }
Check(analyzer.GetSpectrum().Max() > .1f, "FFT accumulates small output reads into complete analysis windows");
analyzer.Enabled = false;
Check(analyzer.GetSpectrum().All(value => value == 0), "reduced motion disables and clears analysis");

var fixtures = args.FirstOrDefault(arg => !arg.StartsWith("--"));
if (!string.IsNullOrEmpty(fixtures))
{
    foreach (var extension in new[] { ".wav", ".flac", ".m4a", ".mp3" })
    {
        var path = Path.Combine(fixtures, "fixture" + extension);
        using var decoder = AudioDecoder.Open(path, 48000, TimeSpan.Zero, true);
        var buffer = new float[4096];
        long samples = 0;
        double energy = 0;
        int read;
        while ((read = decoder.Source.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples += read;
            for (var i = 0; i < read; i++)
                energy += buffer[i] * buffer[i];
        }
        var seconds = samples / 96000d;
        Check(seconds is > 1.8 and < 2.2 && energy > 1,
            $"native Media Foundation decodes {extension} ({seconds:0.000}s, nonzero samples)");
        using var seeked = AudioDecoder.Open(path, 48000, TimeSpan.FromSeconds(1), false);
        samples = 0;
        while ((read = seeked.Source.Read(buffer, 0, buffer.Length)) > 0)
            samples += read;
        Check(samples / 96000d is > .8 and < 1.2, $"native decoder seeks within {extension} ({samples / 96000d:0.000}s remain)");
    }
    try
    {
        AudioDecoder.Open(Path.Combine(fixtures, "not-present.flac"), 48000, TimeSpan.Zero, false);
        throw new InvalidOperationException("Missing files must fail.");
    }
    catch (AudioPlaybackException error)
    {
        Check(error.Message.Contains("missing", StringComparison.OrdinalIgnoreCase), "missing-file errors explain the recovery");
    }
}

if (args.Contains("--devices"))
{
    using var player = new AudioPlayer();
    var devices = player.GetOutputDevices();
    Check(devices.Count > 0, "Windows reports available render devices");
    foreach (var device in devices)
        Console.WriteLine("DEVICE " + device.Name);
}

if (args.Contains("--playback") && fixtures is not null)
{
    using var player = new AudioPlayer();
    player.SetVolume(0); // Exercise the real endpoint without playing a test tone aloud.
    player.AnalysisEnabled = false;
    var transitions = 0;
    var ends = 0;
    AudioTrackStartedEventArgs? transition = null;
    player.NextTrackStarted += (_, args) => { transition = args as AudioTrackStartedEventArgs; Interlocked.Increment(ref transitions); Console.WriteLine("EVENT next " + player.CurrentPath); };
    player.PlaybackStopped += (_, _) => { Interlocked.Increment(ref ends); Console.WriteLine("EVENT end " + player.LastError); };
    player.PlaybackError += (_, error) => Console.WriteLine("EVENT error " + error.Exception);
    var first = Path.GetFullPath(Path.Combine(fixtures, "fixture.wav"));
    var second = Path.GetFullPath(Path.Combine(fixtures, "fixture.flac"));
    player.Play(first, false);
    var pausedVersion = player.PlaybackVersion;
    Thread.Sleep(100);
    Check(!player.IsPlaying && player.Position == TimeSpan.Zero && player.CurrentPath == first,
        "initial Play(false) loads the track without starting the endpoint");
    player.Seek(TimeSpan.FromSeconds(.5));
    Check(!player.IsPlaying && Math.Abs(player.Position.TotalSeconds - .5) < .002
        && player.PlaybackVersion > pausedVersion, "session restore seeks silently and invalidates old playback callbacks");
    Check(player.PlayAsync(first, false, TimeSpan.FromSeconds(.4)).GetAwaiter().GetResult() && !player.IsPlaying
        && Math.Abs(player.Position.TotalSeconds - .4) < .002,
        "asynchronous decoder open restores an initially paused session and position");
    var pending = player.PlayAsync(first);
    player.Stop();
    pending.GetAwaiter().GetResult();
    Check(player.CurrentPath is null && !player.IsPlaying, "a stopped asynchronous open cannot resurrect playback");
    var superseded = player.PlayAsync(first, false);
    var latest = player.PlayAsync(second, false);
    Task.WaitAll(superseded, latest);
    Check(latest.Result && player.CurrentPath == second, "the latest asynchronous open wins overlapping requests");
    var rapid = Enumerable.Range(0, 48).Select(index => player.PlayAsync(index % 2 == 0 ? first : second, false)).ToArray();
    Task.WaitAll(rapid);
    Check(rapid[^1].Result && player.CurrentPath == second && !player.IsPlaying,
        "a rapid burst of asynchronous opens leaves only the latest requested paused file active");
    var stoppedBurst = Enumerable.Range(0, 32).Select(_ => player.PlayAsync(first, false)).ToArray();
    player.Stop(); Task.WaitAll(stoppedBurst);
    Check(player.CurrentPath is null && !player.IsPlaying,
        "stopping a burst invalidates active and queued decoder opens without resurrecting a session");
    var drainFile = Path.Combine(fixtures, "drain-" + Guid.NewGuid().ToString("N") + ".wav");
    File.Copy(first, drainFile);
    try
    {
        var draining = Enumerable.Range(0, 32).Select(_ => player.PlayAsync(drainFile, false)).ToArray();
        player.StopAsync().GetAwaiter().GetResult();
        using (var exclusive = new FileStream(drainFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(player.CurrentPath is null && !player.IsPlaying && exclusive.Length > 0,
                "asynchronous stop drains pending decoder ownership before a file can be opened exclusively");
        Task.WaitAll(draining);
    }
    finally { File.Delete(drainFile); }
    using (var cancelled = new CancellationTokenSource())
    {
        player.Play(first, false); cancelled.Cancel();
        try { player.StopAsync(cancelled.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("A cancelled drain must cancel its wait."); }
        catch (OperationCanceledException) { }
        Check(player.CurrentPath is null && !player.IsPlaying,
            "cancelling a decoder-drain wait does not undo the immediate stop");
    }

    using (var disposedPlayer = new AudioPlayer())
    {
        disposedPlayer.SetVolume(0);
        var discarded = Enumerable.Range(0, 24).Select(_ => disposedPlayer.PlayAsync(first, false)).ToArray();
        disposedPlayer.Dispose(); Task.WaitAll(discarded);
        Check(discarded.All(task => task.IsCompletedSuccessfully) && disposedPlayer.CurrentPath is null,
            "disposing during queued decoder opens completes stale requests and keeps playback closed");
    }
    player.Play(first);
    player.PrepareNext(second);
    for (var repeated = 0; repeated < 250; repeated++) player.PrepareNext(second);
    Check(SpinWait.SpinUntil(() => Volatile.Read(ref transitions) == 1, 10000), $"real WASAPI stream reaches its queued transition (position {player.Position}, playing {player.IsPlaying}, error {player.LastError})");
    Check(player.CurrentPath == second && player.Position < TimeSpan.FromSeconds(.3)
        && Volatile.Read(ref ends) == 0, "rendered transition updates path and position without stopping the output");
    Check(transition is not null && transition.Path == second && transition.Version == player.PlaybackVersion
        && Math.Abs(transition.BoundaryStart - 2) < .0001 && transition.BoundaryStart == player.CurrentBoundaryStart,
        "transition arguments identify the exact session and rendered boundary");
    Check(SpinWait.SpinUntil(() => Volatile.Read(ref ends) == 1, 5000), "real WASAPI stream reports one natural queue end");
    Check(!player.IsPlaying && player.Position == player.Duration, "natural queue end retains the final playback position");
    player.Play(first);
    Check(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(.2), 2000), "WASAPI position follows rendered audio");
    player.PausePlayback();
    var paused = player.Position;
    Thread.Sleep(200);
    Check(!player.IsPlaying && Math.Abs((player.Position - paused).TotalMilliseconds) < 1, "paused position remains stable while the endpoint is idle");
    player.Seek(TimeSpan.FromSeconds(.7));
    Check(!player.IsPlaying && Math.Abs(player.Position.TotalSeconds - .7) < .002, "seeking while paused preserves paused state and position");
    player.Resume();
    Check(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(.9), 2000), "resume continues from the requested seek position");
    var deviceId = player.GetOutputDevices()[0].Id;
    var beforeDeviceChange = player.Position;
    player.SetOutputDevice(deviceId);
    Check(player.OutputDeviceId == deviceId && player.IsPlaying
        && Math.Abs((player.Position - beforeDeviceChange).TotalSeconds) < .2, "changing output preserves the track position and playback state");
    player.Stop();
    Thread.Sleep(100);
    Check(Volatile.Read(ref ends) == 1 && player.CurrentPath is null, "manual stop emits no natural-end event and releases the current file");
    player.Play(first);
    var activePath = player.CurrentPath;
    var activeVersion = player.PlaybackVersion;
    try { player.SetOutputDevice("XTONE-fixture-nonexistent-output-device"); throw new InvalidOperationException("A nonexistent device must fail."); }
    catch (AudioPlaybackException) { }
    Check(player.CurrentPath == activePath && player.PlaybackVersion == activeVersion && player.IsPlaying,
        "rejecting a disconnected output selection preserves the existing playback session");
    player.Play(first);
    player.PrepareNext(Path.Combine(fixtures, "missing-next.flac"));
    Thread.Sleep(180);
    Check(player.CurrentPath == first && player.IsPlaying && player.LastError is null,
        "a failed next-file preparation does not interrupt or mark the current track as failed");
    player.Stop();

}
Console.WriteLine($"{passed} audio checks passed.");

static AudioSegment Segment(string path, float[] values, IDisposable? owner = null) =>
    new(path, TimeSpan.FromSeconds(values.Length / 20d), TimeSpan.Zero, new ArrayProvider(values), owner);

internal sealed class ArrayProvider(float[] values, int sampleRate = 10) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = Math.Min(count, values.Length - _position);
        Buffer.BlockCopy(values, _position * sizeof(float), buffer, offset * sizeof(float), read * sizeof(float));
        _position += read;
        return read;
    }
}

internal sealed class DisposalCounter : IDisposable
{
    public int Count { get; private set; }
    public void Dispose() => Count++;
}

internal sealed class CountedProvider(ISampleProvider source) : ISampleProvider
{
    public int Reads { get; private set; }
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count) { Reads++; return source.Read(buffer, offset, count); }
}

