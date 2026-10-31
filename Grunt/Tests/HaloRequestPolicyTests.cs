using System.Net;
using System.Net.Http.Headers;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Models;

namespace Tests;

/// <summary>
/// Backoff and auth-retry behaviour of <see cref="HaloRequestPolicy"/> (the policy behind every generated
/// <see cref="HaloInfiniteClientFactory"/> method), driven through a real <see cref="HaloInfiniteClient"/> on a fake handler.
/// </summary>
public class HaloRequestPolicyTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script;
        public int Calls { get; private set; }

        public ScriptedHandler(params Func<HttpResponseMessage>[] script) => _script = new(script);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var next = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            return Task.FromResult(next());
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode code, TimeSpan? retryAfter = null) => () =>
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent("{}") };
        if (retryAfter is { } ra)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        }
        return response;
    };

    private static Func<HttpResponseMessage> Ok() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"xuid\":\"2533274800000000\",\"gamertag\":\"Grunt\"}"),
    };

    private static Func<HttpResponseMessage> Throw() => () => throw new HttpRequestException("connection reset");

    private sealed class Harness
    {
        public List<TimeSpan> Delays { get; } = new();
        public List<bool> ClientRequests { get; } = new();
        public ScriptedHandler Handler { get; }
        public HaloRequestPolicy Policy { get; }

        public Harness(HaloRetryOptions options, params Func<HttpResponseMessage>[] script)
        {
            Handler = new ScriptedHandler(script);
            Policy = new HaloRequestPolicy(options, delay: d => { Delays.Add(d); return Task.CompletedTask; }, random: () => 0.5);
        }

        public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Run() => Policy.ExecuteAsync(
            "UserByGamertag",
            force =>
            {
                ClientRequests.Add(force);
                return Task.FromResult<IHaloInfiniteClient>(new HaloInfiniteClient(new HttpClient(Handler), "spartan-token"));
            },
            client => client.UserByGamertag("Grunt"),
            r => r?.Error);
    }

    private static HaloRetryOptions NoJitter(int maxAttempts = 4) => new()
    {
        MaxAttempts = maxAttempts,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = TimeSpan.FromSeconds(1),
        UseJitter = false,
    };

    [Test]
    public async Task Retries429HonouringRetryAfter()
    {
        var h = new Harness(NoJitter(), Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(result.Result.gamertag, Is.EqualTo("Grunt"));
        Assert.That(h.Handler.Calls, Is.EqualTo(2));
        Assert.That(h.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
    }

    [Test]
    public async Task Transient5xxBacksOffExponentiallyAndStopsAtMaxAttempts()
    {
        var h = new Harness(NoJitter(maxAttempts: 4), Status(HttpStatusCode.ServiceUnavailable));

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(503));
        Assert.That(h.Handler.Calls, Is.EqualTo(4));
        Assert.That(h.Delays, Is.EqualTo(new[]
        {
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400),
        }));
    }

    [Test]
    public async Task NetworkErrorIsRetried()
    {
        var h = new Harness(NoJitter(), Throw(), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(h.Handler.Calls, Is.EqualTo(2));
        Assert.That(h.Delays, Has.Count.EqualTo(1));
    }

    [Test]
    public void NetworkErrorIsRethrownWhenAttemptsRunOut()
    {
        var h = new Harness(NoJitter(maxAttempts: 2), Throw());

        Assert.ThrowsAsync<HttpRequestException>(() => h.Run());
        Assert.That(h.Handler.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task AuthFailureRebuildsClientOnceWithoutDelay()
    {
        var h = new Harness(NoJitter(), Status(HttpStatusCode.Unauthorized), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(h.ClientRequests, Is.EqualTo(new[] { false, true }));
        Assert.That(h.Delays, Is.Empty);
    }

    [Test]
    public async Task RepeatedAuthFailureIsReturnedAfterOneRetry()
    {
        var h = new Harness(NoJitter(), Status(HttpStatusCode.Forbidden));

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(403));
        Assert.That(h.Handler.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task AuthRetryDoesNotUseTheTransientBudget()
    {
        var h = new Harness(NoJitter(maxAttempts: 2), Status(HttpStatusCode.Unauthorized), Status(HttpStatusCode.TooManyRequests), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(h.Handler.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task NotFoundIsNotRetried()
    {
        var h = new Harness(NoJitter(), Status(HttpStatusCode.NotFound), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(404));
        Assert.That(h.Handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task RetryAfterLongerThanMaxIsNotWaitedOut()
    {
        var options = NoJitter();
        options.MaxRetryAfter = TimeSpan.FromSeconds(5);
        var h = new Harness(options, Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10)), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(429));
        Assert.That(result.Error.RetryAfter, Is.EqualTo(TimeSpan.FromMinutes(10)));
        Assert.That(h.Delays, Is.Empty);
    }

    [Test]
    public async Task MaxAttemptsOfOneDisablesTransientRetries()
    {
        var h = new Harness(NoJitter(maxAttempts: 1), Status(HttpStatusCode.BadGateway), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(502));
        Assert.That(h.Handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ConfiguredStatusCodesReplaceTheDefaults()
    {
        var options = NoJitter();
        options.RetryStatusCodes = new List<int> { 404 };
        var h = new Harness(options, Status(HttpStatusCode.NotFound), Status(HttpStatusCode.ServiceUnavailable), Ok());

        var result = await h.Run();

        Assert.That(result.Error.Code, Is.EqualTo(503));
        Assert.That(h.Handler.Calls, Is.EqualTo(2));
    }

    [Test]
    public void ComputeDelayCapsAndJitters()
    {
        var options = new HaloRetryOptions { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5), UseJitter = true };

        Assert.Multiple(() =>
        {
            // Equal jitter: between half and all of the exponential delay.
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 1, null, () => 0.0), Is.EqualTo(TimeSpan.FromMilliseconds(500)));
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 1, null, () => 0.999999).TotalMilliseconds, Is.EqualTo(1000).Within(1));
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 3, null, () => 0.5), Is.EqualTo(TimeSpan.FromSeconds(3)));
            // Capped at MaxDelay before jitter.
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 20, null, () => 0.0), Is.EqualTo(TimeSpan.FromMilliseconds(2500)));
            // Retry-After wins over backoff and is capped at MaxRetryAfter.
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 1, TimeSpan.FromSeconds(7)), Is.EqualTo(TimeSpan.FromSeconds(7)));
            Assert.That(HaloRequestPolicy.ComputeDelay(options, 1, TimeSpan.FromHours(1)), Is.EqualTo(options.MaxRetryAfter));
        });
    }

    [Test]
    public void TransientExceptionClassification()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HaloRequestPolicy.IsTransientException(new HttpRequestException()), Is.True);
            Assert.That(HaloRequestPolicy.IsTransientException(new TaskCanceledException("timeout", new TimeoutException())), Is.True);
            Assert.That(HaloRequestPolicy.IsTransientException(new TaskCanceledException()), Is.False);
            Assert.That(HaloRequestPolicy.IsTransientException(new InvalidOperationException()), Is.False);
        });
    }
}
