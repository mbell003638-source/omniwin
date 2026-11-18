using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniWin.App;

/// <summary>Turns recorded speech into text. The app's only use of a speech model; nothing here sends input.</summary>
internal interface ITranscriber
{
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
}

/// <summary>
/// One local speech model the owner can choose. The file is downloaded only when the owner presses the button, from an address
/// pinned to one commit, and it is accepted only when its size and SHA-256 are the ones recorded here.
/// </summary>
internal sealed record SpeechModel(string Id, string Name, int SizeMb, string File, string Url, string Note, long Bytes, string Sha256);

internal static class SpeechModels
{
    /// <summary>
    /// The commit of huggingface.co/ggerganov/whisper.cpp these files are taken from. Sizes and SHA-256 values are the ones that
    /// repository's own file listing reports for this commit (read 2026-11-16). A moving branch is never used.
    /// </summary>
    public const string Commit = "5359861c739e955e79d9a303bcbc70fb988958b1";

    private const string Base = "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + Commit + "/";

    public static readonly IReadOnlyList<SpeechModel> All =
    [
        new("tiny", "Whisper tiny", 75, "ggml-tiny.bin", Base + "ggml-tiny.bin", "fastest, basic quality",
            77691713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new("base", "Whisper base", 142, "ggml-base.bin", Base + "ggml-base.bin", "fast, good quality (a sensible start on this PC)",
            147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "Whisper small", 466, "ggml-small.bin", Base + "ggml-small.bin", "slower, better quality",
            487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("turbo-q5", "Whisper large v3 turbo (compact)", 574, "ggml-large-v3-turbo-q5_0.bin", Base + "ggml-large-v3-turbo-q5_0.bin", "good quality, slow without a GPU",
            574041195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        new("medium", "Whisper medium", 1500, "ggml-medium.bin", Base + "ggml-medium.bin", "high quality, slow",
            1533763059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        new("turbo", "Whisper large v3 turbo", 1600, "ggml-large-v3-turbo.bin", Base + "ggml-large-v3-turbo.bin", "high quality, slow without a GPU",
            1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        new("large-q5", "Whisper large v3 (compact)", 1081, "ggml-large-v3-q5_0.bin", Base + "ggml-large-v3-q5_0.bin", "best quality, very slow without a GPU",
            1081140203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        new("large", "Whisper large v3", 3000, "ggml-large-v3.bin", Base + "ggml-large-v3.bin", "best quality, very slow without a GPU",
            3095033483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
    ];

    public static SpeechModel? Find(string id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string FolderIn(string dataDir) => Path.Combine(dataDir, "speech-models");

    public static string PathOf(string dataDir, SpeechModel model) => Path.Combine(FolderIn(dataDir), model.File);

    /// <summary>
    /// Downloads one model into the data folder. Called only from the Settings window's Download button. The file is written
    /// under a temporary name, checked against the recorded size and SHA-256, and renamed only then, so neither a broken download
    /// nor a different file ever looks like a model.
    /// </summary>
    public static async Task DownloadAsync(string dataDir, SpeechModel model, IProgress<int>? percent, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(FolderIn(dataDir));
        var target = PathOf(dataDir, model);
        var partial = target + ".part";
        try
        {
            await FetchAndCheckAsync(model, partial, percent, ct, handler).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled, broken mid-stream or failed its check: never leave this app's own unfinished file behind.
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        File.Move(partial, target, overwrite: true);
    }

    private static async Task FetchAndCheckAsync(SpeechModel model, string partial, IProgress<int>? percent, CancellationToken ct, HttpMessageHandler? handler)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var from = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            var last = -1;
            int read;
            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total is > 0 && (int)(done * 100 / total.Value) is var now && now != last) percent?.Report(last = now);
            }
        }

        // Accept the file only when it is exactly the one recorded for this model (review 27, S1).
        string actual;
        long length;
        await using (var check = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            length = check.Length;
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
        }

        if (length != model.Bytes || !string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The downloaded file is not the expected {model.Name} (its size or checksum is different), so it was thrown away. Nothing was installed.");
        }
    }
}

/// <summary>
/// Speech to text with whisper.cpp's command-line program (whisper-cli.exe), which runs fully on this PC. The recording is written
/// to a temp file for the program to read and removed straight afterwards; it is never kept.
/// </summary>
internal sealed class WhisperCliTranscriber(string enginePath, string modelPath, string language = "auto", int timeoutSeconds = 120) : ITranscriber
{
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (!File.Exists(enginePath)) throw new FileNotFoundException("The speech program (whisper-cli.exe) was not found. Set its path in Settings.", enginePath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The speech model has not been downloaded yet. Use the Download button in Settings.", modelPath);

        var file = Path.Combine(Path.GetTempPath(), "omniwin-speech-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(file, wav, ct).ConfigureAwait(false);
            var info = new ProcessStartInfo(enginePath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-m", modelPath, "-f", file, "-nt", "-np", "-l", language }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The speech program could not be started.");
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException($"The speech program ended with code {process.ExitCode}.");
                return Clean(output);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(ct.IsCancellationRequested ? "Cancelled." : "The speech model took too long. Try a smaller model.");
            }
        }
        finally
        {
            try
            {
                File.Delete(file);   // the temp recording this call created
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>whisper.cpp prints one piece of text per line, and bracketed notes such as [BLANK_AUDIO] for silence.</summary>
    internal static string Clean(string output)
    {
        var parts = new List<string>();
        foreach (var raw in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || (line.StartsWith('[') && line.EndsWith(']')) || (line.StartsWith('(') && line.EndsWith(')'))) continue;
            parts.Add(line);
        }

        return string.Join(' ', parts).Trim();
    }
}

/// <summary>A WAV file (16 kHz, one channel, 16 bits: what whisper.cpp wants) from raw samples.</summary>
internal static class Wav
{
    public const int Rate = 16000;

    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[44 + pcm.Length];
        using (var writer = new BinaryWriter(new MemoryStream(wav)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);          // PCM
            writer.Write((short)1);          // mono
            writer.Write(Rate);
            writer.Write(Rate * 2);          // bytes per second
            writer.Write((short)2);          // bytes per sample frame
            writer.Write((short)16);         // bits
            writer.Write("data"u8);
            writer.Write(pcm.Length);
        }

        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }

    public static double Seconds(int pcmBytes) => pcmBytes / (double)(Rate * 2);
}

/// <summary>
/// Records the default microphone through Windows' own wave-in interface (no extra library). Recording happens only between
/// Start and Stop, which the owner triggers with a hotkey; nothing listens in the background. The samples stay in memory.
/// </summary>
internal sealed class MicRecorder : IDisposable
{
    public const int MaxSeconds = 120;
    private const int BufferBytes = Wav.Rate * 2 / 5;   // 200 ms
    private const int Buffers = 4;
    private const uint WaveMapper = 0xFFFFFFFF, WhdrDone = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr handle);

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private readonly IntPtr[] _headers = new IntPtr[Buffers];
    private IntPtr _handle;
    private Thread? _pump;
    private volatile bool _recording;

    public bool Recording => _recording;

    /// <summary>Starts recording. Throws with a plain message when there is no usable microphone.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_recording) return;
            var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = Wav.Rate, AvgBytesPerSec = Wav.Rate * 2, BlockAlign = 2, BitsPerSample = 16 };
            if (waveInOpen(out _handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                _handle = IntPtr.Zero;
                throw new InvalidOperationException("No microphone could be opened. Check that one is connected and that Windows lets desktop apps use it.");
            }

            _pcm.SetLength(0);
            for (var i = 0; i < Buffers; i++)
            {
                var header = Marshal.AllocHGlobal(HeaderSize + BufferBytes);
                Marshal.StructureToPtr(new WaveHeader { Data = header + HeaderSize, BufferLength = BufferBytes }, header, false);
                _headers[i] = header;
                waveInPrepareHeader(_handle, header, HeaderSize);
                waveInAddBuffer(_handle, header, HeaderSize);
            }

            waveInStart(_handle);
            _recording = true;
            _pump = new Thread(Pump) { IsBackground = true, Name = "OmniWin microphone" };
            _pump.Start();
        }
    }

    private void Pump()
    {
        var started = Stopwatch.StartNew();
        while (_recording)
        {
            Collect(requeue: true);
            if (started.Elapsed.TotalSeconds > MaxSeconds) _recording = false;   // never record without end
            Thread.Sleep(40);
        }
    }

    private void Collect(bool requeue)
    {
        lock (_pcm)
        {
            foreach (var header in _headers)
            {
                if (header == IntPtr.Zero) continue;
                var h = Marshal.PtrToStructure<WaveHeader>(header);
                if ((h.Flags & WhdrDone) == 0) continue;
                if (h.BytesRecorded > 0)
                {
                    var chunk = new byte[h.BytesRecorded];
                    Marshal.Copy(h.Data, chunk, 0, chunk.Length);
                    _pcm.Write(chunk);
                }

                h.BytesRecorded = 0;
                h.Flags &= ~WhdrDone;
                Marshal.StructureToPtr(h, header, false);
                if (requeue && _recording) waveInAddBuffer(_handle, header, HeaderSize);
            }
        }
    }

    /// <summary>Stops and returns what was recorded as a WAV file in memory (empty when nothing was recorded).</summary>
    public byte[] Stop()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return [];
            _recording = false;
            _pump?.Join(1000);
            waveInReset(_handle);            // hands every buffer back, marked done
            Collect(requeue: false);
            for (var i = 0; i < Buffers; i++)
            {
                if (_headers[i] == IntPtr.Zero) continue;
                waveInUnprepareHeader(_handle, _headers[i], HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                _headers[i] = IntPtr.Zero;
            }

            waveInClose(_handle);
            _handle = IntPtr.Zero;
            lock (_pcm)
            {
                var pcm = _pcm.ToArray();
                _pcm.SetLength(0);
                return pcm.Length == 0 ? [] : Wav.FromPcm16(pcm);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _pcm.Dispose();
    }
}

internal sealed record SpokenResult(bool Ok, string Text, string Message);

/// <summary>What happens after a recording stops: speech to text, then either typed at the cursor or handed to the command box.</summary>
internal sealed class Dictation(IToolCaller omniwin, ITranscriber transcriber)
{
    public const double MinSeconds = 0.3;

    public async Task<SpokenResult> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (wav.Length <= 44 || Wav.Seconds(wav.Length - 44) < MinSeconds) return new SpokenResult(false, string.Empty, "Nothing was recorded.");
        string text;
        try
        {
            text = await transcriber.TranscribeAsync(wav, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SpokenResult(false, string.Empty, ex.Message);
        }

        return text.Length == 0 ? new SpokenResult(false, string.Empty, "No words were heard.") : new SpokenResult(true, text, text);
    }

    /// <summary>Dictation: the words are put at the cursor through OmniWin's text_insert, so every safety check applies.</summary>
    public async Task<SpokenResult> DictateAsync(byte[] wav, CancellationToken ct)
    {
        var heard = await TranscribeAsync(wav, ct).ConfigureAwait(false);
        if (!heard.Ok) return heard;
        var result = PlainWords.Explain(await omniwin.CallAsync("text_insert", new { text = heard.Text }, ct).ConfigureAwait(false));
        return result.Outcome switch
        {
            Outcome.Done => new SpokenResult(true, heard.Text, "Typed: " + heard.Text),
            Outcome.Unverified => new SpokenResult(true, heard.Text, "Typed, but OmniWin could not read it back. Please look: " + heard.Text),
            _ => new SpokenResult(false, heard.Text, "Not typed. " + result.Headline + (result.Detail is null ? string.Empty : " " + result.Detail)),
        };
    }
}
