using FluentAssertions;
using RedisSharp.Core.Storage;
using Xunit;

namespace RedisSharp.Tests;

public class KeyValueStoreTests
{
    private readonly FakeClock _clock = new();
    private readonly KeyValueStore _store;

    public KeyValueStoreTests() => _store = new KeyValueStore(_clock);

    [Fact]
    public void Set_then_get_returns_value()
    {
        _store.Set("k", "v");
        _store.TryGet("k", out var v).Should().BeTrue();
        v.Should().Be("v");
    }

    [Fact]
    public void Get_missing_key_returns_false()
    {
        _store.TryGet("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void Key_expires_after_ttl()
    {
        _store.Set("k", "v", TimeSpan.FromSeconds(10));

        _clock.Advance(TimeSpan.FromSeconds(9));
        _store.Exists("k").Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _store.Exists("k").Should().BeFalse();
        _store.TryGet("k", out _).Should().BeFalse();
    }

    [Fact]
    public void Overwriting_key_without_ttl_clears_old_ttl()
    {
        _store.Set("k", "v1", TimeSpan.FromSeconds(1));
        _store.Set("k", "v2");

        _clock.Advance(TimeSpan.FromHours(1));
        _store.TryGet("k", out var v).Should().BeTrue();
        v.Should().Be("v2");
        _store.GetTtl("k").Should().BeNull();
    }

    [Fact]
    public void SetIfAbsent_respects_existing_live_key_but_not_expired_one()
    {
        _store.Set("k", "old", TimeSpan.FromSeconds(1));
        _store.SetIfAbsent("k", "new").Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(2));
        _store.SetIfAbsent("k", "new").Should().BeTrue();
        _store.TryGet("k", out var v).Should().BeTrue();
        v.Should().Be("new");
    }

    [Fact]
    public void SetIfPresent_only_updates_existing_keys()
    {
        _store.SetIfPresent("k", "v").Should().BeFalse();
        _store.Set("k", "v0");
        _store.SetIfPresent("k", "v1").Should().BeTrue();
        _store.TryGet("k", out var v).Should().BeTrue();
        v.Should().Be("v1");
    }

    [Fact]
    public void Delete_returns_whether_a_live_key_was_removed()
    {
        _store.Set("k", "v");
        _store.Delete("k").Should().BeTrue();
        _store.Delete("k").Should().BeFalse();
    }

    [Fact]
    public void Delete_of_expired_key_reports_false()
    {
        _store.Set("k", "v", TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(5));
        _store.Delete("k").Should().BeFalse();
    }

    [Fact]
    public void Increment_treats_missing_key_as_zero()
    {
        _store.Increment("n", 1).Should().Be(1);
        _store.Increment("n", 10).Should().Be(11);
        _store.Increment("n", -11).Should().Be(0);
    }

    [Fact]
    public void Increment_preserves_ttl()
    {
        _store.Set("n", "5", TimeSpan.FromSeconds(30));
        _store.Increment("n", 1).Should().Be(6);
        _store.GetTtl("n").Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Increment_rejects_non_integer_values()
    {
        _store.Set("s", "hello");
        var act = () => _store.Increment("s", 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not an integer*");
    }

    [Fact]
    public void Increment_rejects_overflow()
    {
        _store.Set("n", long.MaxValue.ToString());
        var act = () => _store.Increment("n", 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*overflow*");
    }

    [Fact]
    public void GetTtl_distinguishes_missing_persistent_and_expiring()
    {
        _store.GetTtl("missing").Should().Be(TimeSpan.MinValue);

        _store.Set("p", "v");
        _store.GetTtl("p").Should().BeNull();

        _store.Set("e", "v", TimeSpan.FromSeconds(10));
        _clock.Advance(TimeSpan.FromSeconds(3));
        _store.GetTtl("e").Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void Expire_and_persist_round_trip()
    {
        _store.Expire("missing", TimeSpan.FromSeconds(1)).Should().BeFalse();

        _store.Set("k", "v");
        _store.Persist("k").Should().BeFalse("key has no TTL yet");

        _store.Expire("k", TimeSpan.FromSeconds(5)).Should().BeTrue();
        _store.Persist("k").Should().BeTrue();

        _clock.Advance(TimeSpan.FromHours(1));
        _store.Exists("k").Should().BeTrue();
    }

    [Fact]
    public void Keys_filters_by_glob_and_skips_expired()
    {
        _store.Set("user:1", "a");
        _store.Set("user:2", "b");
        _store.Set("order:1", "c");
        _store.Set("user:3", "d", TimeSpan.FromSeconds(1));
        _clock.Advance(TimeSpan.FromSeconds(2));

        _store.Keys("user:*").Should().BeEquivalentTo("user:1", "user:2");
        _store.Keys("*:1").Should().BeEquivalentTo("user:1", "order:1");
        _store.Keys("user:?").Should().BeEquivalentTo("user:1", "user:2");
        _store.Keys("*").Should().HaveCount(3);
    }

    [Fact]
    public void EvictExpired_removes_only_expired_entries()
    {
        _store.Set("a", "1", TimeSpan.FromSeconds(1));
        _store.Set("b", "2", TimeSpan.FromSeconds(100));
        _store.Set("c", "3");
        _clock.Advance(TimeSpan.FromSeconds(2));

        _store.EvictExpired().Should().Be(1);
        _store.Count.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_increments_are_atomic()
    {
        const int workers = 16, perWorker = 2_000;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
                _store.Increment("counter", 1);
        })));

        _store.TryGet("counter", out var v).Should().BeTrue();
        v.Should().Be((workers * perWorker).ToString());
    }
}
