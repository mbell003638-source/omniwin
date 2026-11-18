using System.Net;
using System.Text;

namespace OmniWin.App.Tests;

internal sealed class FakeTranscriber(string answer) : ITranscriber
{
    public int Calls;

    public Exception? Throw { get; set; }

    public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        Calls++;
        return Throw is null ? Task.FromResult(answer) : Task.FromException<string>(Throw);
    }
}

internal sealed class InsertOnly(string answer) : IToolCaller
{
    public List<(string Tool, string Text)> Calls { get; } = [];

    public Task<string> CallAsync(string tool, object? arguments, CancellationToken ct)
    {
        Calls.Add((tool, System.Text.Json.JsonSerializer.SerializeToNode(arguments)!["text"]!.GetValue<string>()));
        return Task.FromResult(answer);
    }
}

/// <summary>Voice and dictation logic with stand-ins: no microphone, no speech model, no download from the internet, no input.</summary>
public sealed class SpeechTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Sound(double seconds) => Wav.FromPcm16(new byte[(int)(Wav.Rate * 2 * seconds)]);

    [Fact]
    public void A_recording_becomes_a_16_kHz_mono_16_bit_wav()
    {
        var wav = Wav.FromPcm16([1, 2, 3, 4]);

        Assert.Equal(48, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));          // one channel
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(4, BitConverter.ToInt32(wav, 40));
        Assert.Equal([1, 2, 3, 4], wav[44..]);
        Assert.Equal(1.0, Wav.Seconds(32000));
    }

    [Theory]
    [InlineData(" Hello there.\n General Kenobi.\n", "Hello there. General Kenobi.")]
    [InlineData("[BLANK_AUDIO]\n", "")]
    [InlineData(" (wind blowing)\n open the report\r\n[MUSIC]\n", "open the report")]
    [InlineData("", "")]
    public void The_speech_programs_output_is_cleaned_of_notes_about_silence_and_noise(string output, string text)
    {
        Assert.Equal(text, WhisperCliTranscriber.Clean(output));
    }

    [Fact]
    public async Task Dictation_types_the_heard_words_at_the_cursor_through_OmniWin()
    {
        var omniwin = new InsertOnly("status=confirmed input_sent=true channel=keys retry=done total=210ms");

        var result = await new Dictation(omniwin, new FakeTranscriber("Dear team, the report is ready.")).DictateAsync(Sound(2), Ct);

        Assert.True(result.Ok);
        Assert.Equal(("text_insert", "Dear team, the report is ready."), Assert.Single(omniwin.Calls));
        Assert.StartsWith("Typed: ", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_OmniWin_refuses_or_cannot_check_is_said_as_it_is_and_never_as_typed()
    {
        var refused = await new Dictation(new InsertOnly("status=denied input_sent=false reason=policy_forbidden retry=no total=1ms\ndetail: The focused control is a password field; it is never read."),
            new FakeTranscriber("my password is hunter2")).DictateAsync(Sound(2), Ct);
        Assert.False(refused.Ok);
        Assert.StartsWith("Not typed.", refused.Message, StringComparison.Ordinal);

        var unverified = await new Dictation(new InsertOnly("status=unverified input_sent=true channel=keys retry=check_state total=200ms"),
            new FakeTranscriber("hello")).DictateAsync(Sound(2), Ct);
        Assert.True(unverified.Ok);
        Assert.Contains("could not read it back", unverified.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Silence_a_too_short_recording_and_a_failing_speech_program_type_nothing()
    {
        var omniwin = new InsertOnly("status=confirmed input_sent=true retry=done total=1ms");
        var silent = new FakeTranscriber(string.Empty);

        Assert.Equal("No words were heard.", (await new Dictation(omniwin, silent).DictateAsync(Sound(2), Ct)).Message);
        Assert.Equal("Nothing was recorded.", (await new Dictation(omniwin, silent).DictateAsync(Sound(0.1), Ct)).Message);
        Assert.Equal("Nothing was recorded.", (await new Dictation(omniwin, silent).DictateAsync([], Ct)).Message);
        Assert.Equal(1, silent.Calls);                                    // the two empty recordings never reached the model

        var broken = new FakeTranscriber("x") { Throw = new FileNotFoundException("The speech model has not been downloaded yet. Use the Download button in Settings.") };
        var result = await new Dictation(omniwin, broken).DictateAsync(Sound(2), Ct);
        Assert.False(result.Ok);
        Assert.Contains("Download button", result.Message, StringComparison.Ordinal);
        Assert.Empty(omniwin.Calls);
    }

    [Fact]
    public async Task A_spoken_command_is_only_turned_into_text_it_is_never_run_by_this_step()
    {
        var omniwin = new InsertOnly("unused");

        var heard = await new Dictation(omniwin, new FakeTranscriber("volume thirty")).TranscribeAsync(Sound(1), Ct);

        Assert.True(heard.Ok);
        Assert.Equal("volume thirty", heard.Text);
        Assert.Empty(omniwin.Calls);
    }

    [Fact]
    public async Task A_missing_speech_program_or_model_is_said_in_words_and_no_temp_recording_is_left()
    {
        using var dir = new TempDir();
        var before = Directory.GetFiles(Path.GetTempPath(), "omniwin-speech-*.wav").Length;

        var noEngine = await Assert.ThrowsAsync<FileNotFoundException>(() => new WhisperCliTranscriber(Path.Combine(dir.Path, "whisper-cli.exe"), Path.Combine(dir.Path, "m.bin")).TranscribeAsync(Sound(1), Ct));
        Assert.Contains("Settings", noEngine.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(dir.Path, "whisper-cli.exe"), "not a program");
        var noModel = await Assert.ThrowsAsync<FileNotFoundException>(() => new WhisperCliTranscriber(Path.Combine(dir.Path, "whisper-cli.exe"), Path.Combine(dir.Path, "m.bin")).TranscribeAsync(Sound(1), Ct));
        Assert.Contains("Download", noModel.Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "omniwin-speech-*.wav").Length);
    }

    [Fact]
    public void The_model_list_offers_several_local_models_each_pinned_to_one_commit_with_its_size_and_checksum()
    {
        Assert.True(SpeechModels.All.Count >= 6);
        Assert.Equal(SpeechModels.All.Count, SpeechModels.All.Select(m => m.Id).Distinct().Count());
        Assert.Matches("^[0-9a-f]{40}$", SpeechModels.Commit);
        Assert.All(SpeechModels.All, m =>
        {
            Assert.Equal($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{SpeechModels.Commit}/{m.File}", m.Url);
            Assert.DoesNotContain("/main/", m.Url, StringComparison.Ordinal);                 // never a moving branch
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.InRange(m.Bytes / 1_000_000.0, m.SizeMb * 0.9, m.SizeMb * 1.1);            // the size shown to the owner is honest
        });
        Assert.Equal(SpeechModels.All.Count, SpeechModels.All.Select(m => m.Sha256).Distinct().Count());
        Assert.NotNull(SpeechModels.Find("BASE"));
        Assert.Null(SpeechModels.Find("gigantic"));
    }

    private sealed class Bytes(HttpStatusCode code, byte[] body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    private static SpeechModel ModelFor(byte[] body, string file = "ggml-test.bin") =>
        new("test", "Test model", 1, file, "https://example.invalid/" + file, "for tests", body.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant());

    [Fact]
    public async Task A_download_is_kept_only_when_its_size_and_checksum_are_the_recorded_ones()
    {
        using var dir = new TempDir();
        var body = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var model = ModelFor(body);
        using var ok = new Bytes(HttpStatusCode.OK, body);

        await SpeechModels.DownloadAsync(dir.Path, model, null, Ct, ok);

        Assert.Equal(model.Url, ok.Asked!.ToString());
        Assert.Equal(body, File.ReadAllBytes(SpeechModels.PathOf(dir.Path, model)));
        Assert.False(File.Exists(SpeechModels.PathOf(dir.Path, model) + ".part"));
    }

    [Fact]
    public async Task Wrong_bytes_a_cut_off_file_and_a_failed_request_install_nothing_and_leave_no_part_file()
    {
        using var dir = new TempDir();
        var body = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();

        // Same length, one byte different: a tampered or replaced file.
        var tampered = (byte[])body.Clone();
        tampered[1234] ^= 0xFF;
        var first = ModelFor(body, "ggml-a.bin");
        using var wrong = new Bytes(HttpStatusCode.OK, tampered);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SpeechModels.DownloadAsync(dir.Path, first, null, Ct, wrong));
        Assert.Contains("thrown away", error.Message, StringComparison.Ordinal);

        // Cut off part-way.
        var second = ModelFor(body, "ggml-b.bin");
        using var shortened = new Bytes(HttpStatusCode.OK, body[..40_000]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechModels.DownloadAsync(dir.Path, second, null, Ct, shortened));

        // The server says no.
        var third = ModelFor(body, "ggml-c.bin");
        using var missing = new Bytes(HttpStatusCode.NotFound, []);
        await Assert.ThrowsAsync<HttpRequestException>(() => SpeechModels.DownloadAsync(dir.Path, third, null, Ct, missing));

        Assert.Empty(Directory.GetFiles(SpeechModels.FolderIn(dir.Path)));     // no model, and no .part left behind
    }

    private sealed class BreaksMidStream(byte[] firstPart) : HttpMessageHandler
    {
        private sealed class Broken(byte[] data) : Stream
        {
            private int _at;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _at; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_at >= data.Length) throw new IOException("the connection was lost");
                var n = Math.Min(count, data.Length - _at);
                Array.Copy(data, _at, buffer, offset, n);
                _at += n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Broken(firstPart)) });
    }

    [Fact]
    public async Task A_download_that_breaks_or_is_cancelled_part_way_leaves_no_part_file()
    {
        using var dir = new TempDir();
        var body = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        var model = ModelFor(body);

        using var broken = new BreaksMidStream(body[..100_000]);
        await Assert.ThrowsAnyAsync<Exception>(() => SpeechModels.DownloadAsync(dir.Path, model, null, Ct, broken));
        Assert.Empty(Directory.GetFiles(SpeechModels.FolderIn(dir.Path)));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var ok = new Bytes(HttpStatusCode.OK, body);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeechModels.DownloadAsync(dir.Path, model, null, cancelled.Token, ok));
        Assert.Empty(Directory.GetFiles(SpeechModels.FolderIn(dir.Path)));
    }

    [Fact]
    public async Task A_model_that_is_already_there_is_not_replaced_by_a_download_that_fails_its_check()
    {
        using var dir = new TempDir();
        var good = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 7)).ToArray();
        var model = ModelFor(good);
        using (var ok = new Bytes(HttpStatusCode.OK, good)) await SpeechModels.DownloadAsync(dir.Path, model, null, Ct, ok);

        using var bad = new Bytes(HttpStatusCode.OK, new byte[10_000]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechModels.DownloadAsync(dir.Path, model, null, Ct, bad));

        Assert.Equal(good, File.ReadAllBytes(SpeechModels.PathOf(dir.Path, model)));
    }

    [Fact]
    public void Voice_hotkeys_are_off_by_default_and_checked_like_the_others()
    {
        var settings = new AppSettings();
        Assert.False(settings.Speech.DictationHotkeyEnabled);
        Assert.False(settings.Speech.VoiceHotkeyEnabled);
        Assert.Empty(settings.Validate());

        Assert.Contains(new AppSettings { Speech = new SpeechSettings { DictationHotkeyEnabled = true, DictationHotkey = "Ctrl+Alt+Shift+K" } }.Validate(), e => e.Contains("stop key", StringComparison.Ordinal));
        Assert.Contains(new AppSettings { CommandHotkeyEnabled = true, CommandHotkey = "Ctrl+Alt+V", Speech = new SpeechSettings { VoiceHotkeyEnabled = true } }.Validate(),
            e => e.Contains("used twice", StringComparison.Ordinal));
        Assert.Contains(new AppSettings { Speech = new SpeechSettings { Model = "gigantic" } }.Validate(), e => e.Contains("speech model", StringComparison.Ordinal));
        Assert.Contains(new AppSettings { Speech = new SpeechSettings { Language = "English" } }.Validate(), e => e.Contains("language", StringComparison.OrdinalIgnoreCase));
    }
}
