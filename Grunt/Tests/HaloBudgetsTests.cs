using Microsoft.Extensions.Time.Testing;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Models;

namespace Tests;

/// <summary>
/// HaloBudgets, and HaloRequestPolicy with them: each call takes room in its operation's budget before it goes to Halo, waits up
/// to the budget's MaxWait for some, else is turned away, never sent, with the time until there is room; a 429 from Halo locks
/// the budget. Time is a FakeTimeProvider.
/// </summary>
public class HaloBudgetsTests
{
    private static readonly HaloBudget Profile = new()
    {
        Operations = ["UserByGamertag", "Users"],
        Limit = 2,
        Window = TimeSpan.FromMinutes(1),
        Lockout = TimeSpan.FromMinutes(5),
        MaxWait = TimeSpan.Zero,
    };

    private static readonly HaloBudget MatchHistory = new()
    {
        Operations = ["StatsGetMatchHistory"],
        Limit = 1,
        Window = TimeSpan.FromSeconds(1),
        Lockout = TimeSpan.FromSeconds(1),
        MaxWait = TimeSpan.FromSeconds(2),
    };

    private static (HaloBudgets Budgets, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        return (new HaloBudgets(new Dictionary<string, HaloBudget> { ["profile"] = Profile, ["matchHistory"] = MatchHistory }, time), time);
    }

    /// <summary>Advances fake time in small steps until <paramref name="task"/> ends (it waits on the fake clock).</summary>
    private static async Task<T> Run<T>(Task<T> task, FakeTimeProvider time, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            await Task.Delay(1);
            time.Advance(step);
        }
        return await task;
    }

    private static Task<HaloApiResultContainer<string, HaloApiErrorContainer>> Answer(int code, TimeSpan? retryAfter = null)
        => Task.FromResult(new HaloApiResultContainer<string, HaloApiErrorContainer>(code == 200 ? "ok" : null!,
            new HaloApiErrorContainer { Code = code, RetryAfter = retryAfter }));

    [Test]
    public async Task AWindowFull_TurnsTheNextAway_WithTheTimeUntilTheOldestCallLeavesIt_SharedByItsOperations()
    {
        var (budgets, time) = Create();

        var first = await budgets.AcquireAsync("UserByGamertag");
        time.Advance(TimeSpan.FromSeconds(20));
        var second = await budgets.AcquireAsync("users");
        var third = await budgets.AcquireAsync("UserByGamertag");
        time.Advance(TimeSpan.FromSeconds(40));
        var fourth = await budgets.AcquireAsync("Users");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.Allowed, second.Allowed), Is.EqualTo((true, true)), "two a minute, gamertags and users together, case-insensitively");
            Assert.That((third.Allowed, third.Reason, third.Budget, third.RetryAfter), Is.EqualTo((false, "window", "profile", TimeSpan.FromSeconds(40))));
            Assert.That(fourth.Allowed, Is.True, "the first call left the window");
        }
    }

    [Test]
    public async Task ACallWaitsUpToMaxWaitForRoom_ThenGoes()
    {
        var (budgets, time) = Create();
        await budgets.AcquireAsync("StatsGetMatchHistory");

        var started = time.GetUtcNow();
        var second = await Run(budgets.AcquireAsync("StatsGetMatchHistory").AsTask(), time, TimeSpan.FromMilliseconds(100));

        Assert.That((second.Allowed, time.GetUtcNow() - started >= TimeSpan.FromSeconds(1)), Is.EqualTo((true, true)));
    }

    [Test]
    public async Task HalosLockout_TurnsEveryCallAway_ForItsLockout_OrHalosLongerRetryAfter()
    {
        var (budgets, time) = Create();

        var locked = budgets.Throttled("UserByGamertag", null);
        var during = await budgets.AcquireAsync("Users");
        var longer = budgets.Throttled("Users", TimeSpan.FromMinutes(8));
        var shorter = budgets.Throttled("Users", TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(8));
        var after = await budgets.AcquireAsync("Users");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(locked, Is.EqualTo(TimeSpan.FromMinutes(5)), "Halo sent no Retry-After: the budget's lockout");
            Assert.That((during.Allowed, during.Reason, during.RetryAfter), Is.EqualTo((false, "lockout", TimeSpan.FromMinutes(5))));
            Assert.That(longer, Is.EqualTo(TimeSpan.FromMinutes(8)), "Halo asked for longer");
            Assert.That(shorter, Is.EqualTo(TimeSpan.FromMinutes(8)), "a shorter lockout never shortens one already running");
            Assert.That(after.Allowed, Is.True);
        }
    }

    [Test]
    public async Task AnOperationInNoBudget_IsNeverLimited()
    {
        var (budgets, _) = Create();

        var turns = new List<HaloBudgetTurn>();
        for (var i = 0; i < 50; i++)
            turns.Add(await budgets.AcquireAsync("StatsGetMatchStats"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(turns.All(t => t.Allowed), Is.True);
            Assert.That(budgets.BudgetOf("StatsGetMatchStats"), Is.Null);
            Assert.That(budgets.Throttled("StatsGetMatchStats", TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public void AnOperationInTwoBudgets_IsAConfigurationError()
        => Assert.Throws<ArgumentException>(() => _ = new HaloBudgets(new Dictionary<string, HaloBudget>
        {
            ["a"] = new() { Operations = ["Users"], Limit = 1 },
            ["b"] = new() { Operations = ["users"], Limit = 1 },
        }));

    [Test]
    public async Task ThePolicy_DoesNotSendACallItsBudgetTurnsAway_AndAnswers429WithTheWait()
    {
        var (budgets, _) = Create();
        var policy = new HaloRequestPolicy(new HaloRetryOptions { UseJitter = false }, budgets: budgets, delay: _ => Task.CompletedTask);
        var sent = 0;

        for (var i = 0; i < 2; i++)
            await policy.ExecuteAsync("Users", _ => Task.FromResult(new object()), _ => { sent++; return Answer(200); }, r => r.Error);
        var third = await policy.ExecuteAsync("Users", _ => Task.FromResult(new object()), _ => { sent++; return Answer(200); }, r => r.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent, Is.EqualTo(2), "the third never reached Halo");
            Assert.That(third.Result, Is.Null);
            Assert.That((third.Error.Code, third.Error.NotSent, third.Error.RetryAfter), Is.EqualTo((429, true, (TimeSpan?)TimeSpan.FromMinutes(1))));
        }
    }

    [Test]
    public async Task ThePolicy_TurnsAwayAResultOfAValueType_Too()
    {
        var (budgets, _) = Create();
        budgets.Throttled("Users", null);
        var policy = new HaloRequestPolicy(budgets: budgets);

        var result = await policy.ExecuteAsync("Users", _ => Task.FromResult(new object()),
            _ => Task.FromResult(new HaloApiResultContainer<bool, HaloApiErrorContainer>(true, new HaloApiErrorContainer { Code = 200 })), r => r.Error);

        Assert.That((result.Result, result.Error.Code, result.Error.NotSent), Is.EqualTo((false, 429, true)));
    }

    [Test]
    public async Task ThePolicy_DoesNotRetryAProfile429_AndGivesTheCallerTheLockoutAsRetryAfter()
    {
        var (budgets, _) = Create();
        var policy = new HaloRequestPolicy(new HaloRetryOptions { MaxAttempts = 4, UseJitter = false }, budgets: budgets, delay: _ => Task.CompletedTask);
        var sent = 0;

        var throttled = await policy.ExecuteAsync("UserByGamertag", _ => Task.FromResult(new object()), _ => { sent++; return Answer(429); }, r => r.Error);
        var next = await policy.ExecuteAsync("Users", _ => Task.FromResult(new object()), _ => { sent++; return Answer(200); }, r => r.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent, Is.EqualTo(1), "neither retried nor the next call sent: the profile service is locked for 5 minutes");
            Assert.That((throttled.Error.Code, throttled.Error.NotSent, throttled.Error.RetryAfter), Is.EqualTo((429, false, (TimeSpan?)TimeSpan.FromMinutes(5))));
            Assert.That((next.Error.Code, next.Error.NotSent), Is.EqualTo((429, true)));
        }
    }

    [Test]
    public async Task ThePolicy_RetriesAMatchHistory429_AfterItsOneSecondLockout()
    {
        var (budgets, time) = Create();
        var policy = new HaloRequestPolicy(new HaloRetryOptions { MaxAttempts = 4, UseJitter = false }, budgets: budgets, delay: _ => Task.CompletedTask);
        var sent = 0;

        var result = await Run(policy.ExecuteAsync("StatsGetMatchHistory", _ => Task.FromResult(new object()),
            _ => ++sent == 1 ? Answer(429, TimeSpan.FromSeconds(1)) : Answer(200), r => r.Error), time, TimeSpan.FromMilliseconds(100));

        Assert.That((result.Result, sent), Is.EqualTo(("ok", 2)));
    }

    [Test]
    public async Task ThePolicy_DoesNotRetryAMatchHistory429_WhenHaloAsksForLongerThanMaxWait()
    {
        var (budgets, _) = Create();
        var policy = new HaloRequestPolicy(new HaloRetryOptions { MaxAttempts = 4, UseJitter = false }, budgets: budgets, delay: _ => Task.CompletedTask);
        var sent = 0;

        var result = await policy.ExecuteAsync("StatsGetMatchHistory", _ => Task.FromResult(new object()),
            _ => { sent++; return Answer(429, TimeSpan.FromSeconds(30)); }, r => r.Error);

        Assert.That((sent, result.Error.Code, result.Error.RetryAfter), Is.EqualTo((1, 429, (TimeSpan?)TimeSpan.FromSeconds(30))));
    }

    [Test]
    public async Task ThePolicy_StillBacksOffA429_ForAnOperationInNoBudget()
    {
        var (budgets, _) = Create();
        var delays = new List<TimeSpan>();
        var policy = new HaloRequestPolicy(new HaloRetryOptions { MaxAttempts = 3, UseJitter = false, BaseDelay = TimeSpan.FromMilliseconds(100) },
            budgets: budgets, delay: d => { delays.Add(d); return Task.CompletedTask; });
        var sent = 0;

        var result = await policy.ExecuteAsync("StatsGetMatchStats", _ => Task.FromResult(new object()),
            _ => ++sent < 3 ? Answer(429) : Answer(200), r => r.Error);

        Assert.That((result.Result, sent, delays.Count), Is.EqualTo(("ok", 3, 2)));
    }
}
