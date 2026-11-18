using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

public sealed class OcrRefStoreTests
{
    private static readonly OcrWord Word = new("Save", new ScreenRect(10, 20, 30, 14), null);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Ids_increase_and_are_never_reused_across_captures()
    {
        var store = new OcrRefStore();
        var captured = DateTimeOffset.UtcNow;

        var first = store.Add(Word, 1, captured);
        var second = store.Add(Word, 1, captured);
        store.Prune();
        var third = store.Add(Word, 2, captured);

        Assert.Equal(["o1", "o2", "o3"], new[] { first, second, third });
    }

    [Fact]
    public void Resolve_returns_the_stored_word_hwnd_and_capture_time()
    {
        var store = new OcrRefStore();
        var captured = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var id = store.Add(Word, 4242, captured);

        Assert.True(store.TryResolve(id, out var word, out var hwnd, out var at));

        Assert.Equal("Save", word.Text);
        Assert.Equal(new ScreenRect(10, 20, 30, 14), word.Box);
        Assert.Equal(id, word.Id);
        Assert.Equal(4242, hwnd);
        Assert.Equal(captured, at);
    }

    [Theory]
    [InlineData("o999")]
    [InlineData("o0")]
    [InlineData("o")]
    [InlineData("")]
    [InlineData("b3")]
    [InlineData("o-1")]
    [InlineData("o1x")]
    [InlineData("O1")]
    public void Unknown_or_malformed_ids_do_not_resolve(string id)
    {
        var store = new OcrRefStore();
        store.Add(Word, 1, DateTimeOffset.UtcNow);

        Assert.False(store.TryResolve(id, out var word, out var hwnd, out var at));
        Assert.Null(word);
        Assert.Equal(0, hwnd);
        Assert.Equal(default, at);
    }

    [Fact]
    public void Concurrent_captures_get_unique_ids_and_a_consistent_store()
    {
        var store = new OcrRefStore();
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 2000, i =>
        {
            var id = store.Add(new OcrWord("w" + i, new ScreenRect(i, i, 1, 1), null), i, DateTimeOffset.UtcNow);
            ids.Add(id);
            Assert.True(store.TryResolve(id, out var word, out var hwnd, out _));
            Assert.Equal("w" + i, word.Text);
            Assert.Equal(i, hwnd);
        });

        Assert.Equal(2000, ids.Distinct().Count());
        Assert.Equal(2000, store.Count);
    }

    [Fact]
    public void An_id_older_than_ten_seconds_still_resolves_so_the_desktop_can_say_stale()
    {
        var time = new ManualTime();
        var store = new OcrRefStore(time);
        var id = store.Add(Word, 1, time.Now);

        time.Now += TimeSpan.FromSeconds(30);
        store.Prune();

        Assert.True(store.TryResolve(id, out _, out _, out var capturedAt));
        Assert.Equal(TimeSpan.FromSeconds(30), time.Now - capturedAt);
    }

    [Fact]
    public void Prune_drops_entries_past_the_retention_time_and_keeps_new_ones()
    {
        var time = new ManualTime();
        var store = new OcrRefStore(time);
        var old = store.Add(Word, 1, time.Now);
        time.Now += TimeSpan.FromSeconds(OcrRefStore.RetentionSeconds + 1);
        var fresh = store.Add(Word, 1, time.Now);

        store.Prune();

        Assert.False(store.TryResolve(old, out _, out _, out _));
        Assert.True(store.TryResolve(fresh, out _, out _, out _));
        Assert.Equal("o2", fresh); // ids are not reused after pruning
    }

    [Fact]
    public void Prune_caps_the_store_by_dropping_the_oldest_entries()
    {
        var store = new OcrRefStore();
        for (var i = 0; i < OcrRefStore.MaxEntries + 10; i++)
        {
            store.Add(Word, 1, DateTimeOffset.UtcNow);
        }

        store.Prune();

        Assert.Equal(OcrRefStore.MaxEntries, store.Count);
        Assert.False(store.TryResolve("o1", out _, out _, out _));
        Assert.True(store.TryResolve("o" + (OcrRefStore.MaxEntries + 10), out _, out _, out _));
    }
}
