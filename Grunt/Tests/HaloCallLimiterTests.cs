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

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
