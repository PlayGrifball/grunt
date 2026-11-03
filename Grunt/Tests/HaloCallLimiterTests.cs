using System.Diagnostics;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Models;

namespace Tests;

/// <summary>
/// HaloCallLimiter: the calls of an operation with a limit wait their turn (a pace, and a number at once); the others go at
/// once. HaloRequestPolicy takes a turn for each attempt and gives it back when the attempt ends, before any wait to retry.
/// </summary>
[TestFixture]
public class HaloCallLimiterTests
{
    private static HaloCallLimiter Limiter(string operation, HaloCallLimit limit) => new(new Dictionary<string, HaloCallLimit> { [operation] = limit });

    [Test]
    public async Task AnOperationWithoutALimitGoesAtOnce()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { PerSecond = 0.1, Concurrency = 1 });
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            (await limiter.AcquireAsync("StatsGetMatchStats")).Dispose();

        Assert.That(limiter.Limits("StatsGetMatchStats"), Is.False);
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task ALimitedOperationIsSpacedToItsPace_WhateverTheCaseOfItsName()
    {
        // 10 a second: one every 100 ms
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { PerSecond = 10 });
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 4; i++)
            (await limiter.AcquireAsync(i % 2 == 0 ? "StatsGetMatchHistory" : "statsgetmatchhistory")).Dispose();

        // The first at once, the next three 100 ms apart
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(260)));
    }

    [Test]
    public async Task OnlyTheConcurrencyRunAtOnce_TheNextWhenOneEnds()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { Concurrency = 2 });
        var first = await limiter.AcquireAsync("StatsGetMatchHistory");
        var second = await limiter.AcquireAsync("StatsGetMatchHistory");

        var third = limiter.AcquireAsync("StatsGetMatchHistory").AsTask();
        await Task.Delay(100);
        Assert.That(third.IsCompleted, Is.False, "Two are running");

        first.Dispose();
        first.Dispose(); // Given back once, however many times
        (await third).Dispose();
        second.Dispose();

        // Both slots free again
        var a = await limiter.AcquireAsync("StatsGetMatchHistory");
        var b = limiter.AcquireAsync("StatsGetMatchHistory").AsTask();
        Assert.That(await Task.WhenAny(b, Task.Delay(1000)), Is.SameAs(b));
        a.Dispose();
        (await b).Dispose();
    }

    [Test]
    public async Task PastItsLongestWait_ACallGoesAnyway()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { Concurrency = 1, MaxWait = TimeSpan.FromMilliseconds(100) });
        using var held = await limiter.AcquireAsync("StatsGetMatchHistory");

        var clock = Stopwatch.StartNew();
        using var late = await limiter.AcquireAsync("StatsGetMatchHistory");

        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90)).And.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task ACallerCancellingStopsWaiting()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { Concurrency = 1 });
        using var held = await limiter.AcquireAsync("StatsGetMatchHistory");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(async () => await limiter.AcquireAsync("StatsGetMatchHistory", cancel.Token));
    }

    [Test]
    public async Task ThePolicyRunsOneAttemptAtATimeUnderAConcurrencyOfOne()
    {
        using var limiter = Limiter("Paged", new HaloCallLimit { Concurrency = 1 });
        var policy = new HaloRequestPolicy(new HaloRetryOptions { UseJitter = false }, limiter: limiter);
        var running = 0;
        var most = 0;

        async Task<HaloApiResultContainer<string, HaloApiErrorContainer>> Call(object _)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return new HaloApiResultContainer<string, HaloApiErrorContainer>("ok", new HaloApiErrorContainer { Code = 200 });
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => policy.ExecuteAsync("Paged", _ => Task.FromResult(new object()), Call, r => r.Error)));

        Assert.That(most, Is.EqualTo(1));
    }

    [Test]
    public async Task ThePolicyGivesTheTurnBackBeforeWaitingToRetry()
    {
        using var limiter = Limiter("Paged", new HaloCallLimit { Concurrency = 1, MaxWait = TimeSpan.FromSeconds(2) });
        var turnFreeWhileWaiting = false;
        var policy = new HaloRequestPolicy(new HaloRetryOptions { UseJitter = false }, limiter: limiter, delay: async _ =>
        {
            // Another call would get the turn at once
            var clock = Stopwatch.StartNew();
            (await limiter.AcquireAsync("Paged")).Dispose();
            turnFreeWhileWaiting = clock.Elapsed < TimeSpan.FromSeconds(1);
        });
        var attempts = 0;

        var result = await policy.ExecuteAsync("Paged", _ => Task.FromResult(new object()), _ => ++attempts == 1
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(new HaloApiResultContainer<string, HaloApiErrorContainer>("ok", new HaloApiErrorContainer { Code = 200 })), r => r.Error);

        Assert.That((result.Result, attempts, turnFreeWhileWaiting), Is.EqualTo(("ok", 2, true)));
    }

    [Test]
    public void AnAdaptivePace_HalvesOnA429_OnceForCallsSentTogether_AndCreepsBackUp()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit
        {
            PerSecond = 4, Adaptive = true, MinPerSecond = 0.5, MaxPerSecond = 4.2, IncreasePerSuccess = 0.1,
        });

        limiter.Report("StatsGetMatchHistory", 429);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(2));
        // The others sent at the old pace answer 429 too: no further slowdown within a second
        limiter.Report("StatsGetMatchHistory", 429);
        limiter.Report("StatsGetMatchHistory", 429);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(2));

        limiter.Report("StatsGetMatchHistory", 200);
        limiter.Report("StatsGetMatchHistory", 404);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(2.2).Within(1e-9), "Any answer but a 429");
        limiter.Report("StatsGetMatchHistory", 0);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(2.2).Within(1e-9), "No answer tells nothing");

        for (var i = 0; i < 100; i++)
            limiter.Report("StatsGetMatchHistory", 200);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(4.2).Within(1e-9), "Up to MaxPerSecond");
    }

    [Test]
    public async Task AnAdaptivePace_NeverGoesUnderItsMinimum()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { PerSecond = 2, Adaptive = true, MinPerSecond = 1.5 });

        limiter.Report("StatsGetMatchHistory", 429);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(1.5));
        await Task.Delay(1100);
        limiter.Report("StatsGetMatchHistory", 429);
        Assert.That(limiter.PerSecond("StatsGetMatchHistory"), Is.EqualTo(1.5));
    }

    [Test]
    public void AFixedPaceAndAnUnlimitedOperation_AreLeftAsTheyAre()
    {
        using var limiter = Limiter("StatsGetMatchHistory", new HaloCallLimit { PerSecond = 2 });

        limiter.Report("StatsGetMatchHistory", 429);
        limiter.Report("StatsGetMatchStats", 429);

        Assert.That((limiter.PerSecond("StatsGetMatchHistory"), limiter.PerSecond("StatsGetMatchStats")), Is.EqualTo((2d, 0d)));
    }

    [Test]
    public async Task ThePolicyTellsTheLimiterHowEachAttemptWent()
    {
        using var limiter = Limiter("Paged", new HaloCallLimit { PerSecond = 50, Adaptive = true, IncreasePerSuccess = 1, MaxPerSecond = 100 });
        var policy = new HaloRequestPolicy(new HaloRetryOptions { UseJitter = false }, limiter: limiter, delay: _ => Task.CompletedTask);
        var attempts = 0;

        var result = await policy.ExecuteAsync("Paged", _ => Task.FromResult(new object()), _ => Task.FromResult(++attempts == 1
            ? new HaloApiResultContainer<string, HaloApiErrorContainer>(null!, new HaloApiErrorContainer { Code = 429 })
            : new HaloApiResultContainer<string, HaloApiErrorContainer>("ok", new HaloApiErrorContainer { Code = 200 })), r => r.Error);

        // Halved on the 429 (25), up by one on the answer (26)
        Assert.That((result.Result, attempts, limiter.PerSecond("Paged")), Is.EqualTo(("ok", 2, 26d)));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
