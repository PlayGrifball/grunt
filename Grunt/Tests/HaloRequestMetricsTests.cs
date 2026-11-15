using System.Diagnostics.Metrics;
using System.Net;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Models;

namespace Tests;

/// <summary>
/// The <c>grunt.halo.requests</c> counter: one measurement per HTTP request sent to Halo, retries included. Each test uses an
/// operation no other test calls, so measurements from other tests don't count.
/// </summary>
public class HaloRequestMetricsTests
{
    private sealed record Measurement(string Operation, string Host, int Status, string? Error);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _measurements = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HaloRequestPolicy.MeterName && instrument.Name == "grunt.halo.requests")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var t = new Dictionary<string, object?>();
                foreach (var tag in tags)
                    t[tag.Key] = tag.Value;
                for (var i = 0; i < value; i++)
                {
                    lock (_measurements)
                        _measurements.Add(new Measurement((string)t["operation"]!, (string)t["host"]!, (int)t["status"]!, t.GetValueOrDefault("error") as string));
                }
            });
            _listener.Start();
        }

        public List<Measurement> For(string operation)
        {
            lock (_measurements)
                return _measurements.Where(m => m.Operation == operation).ToList();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script = new(script);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult((_script.Count > 1 ? _script.Dequeue() : _script.Peek())());
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode code, string body = "{}") => () => new HttpResponseMessage(code) { Content = new StringContent(body) };

    private static Task<HaloApiResultContainer<TResult, HaloApiErrorContainer>> RunWithPolicy<TResult>(
        string operation, HttpMessageHandler handler, Func<IHaloInfiniteClient, Task<HaloApiResultContainer<TResult, HaloApiErrorContainer>>> call)
    {
        var policy = new HaloRequestPolicy(new HaloRetryOptions { MaxAttempts = 3, UseJitter = false }, delay: _ => Task.CompletedTask);
        return policy.ExecuteAsync(operation, _ => Task.FromResult<IHaloInfiniteClient>(new HaloInfiniteClient(new HttpClient(handler), "spartan-token")),
            call, r => r?.Error);
    }

    [Test]
    public async Task EveryAttemptIsCountedWithItsOperationHostAndStatus()
    {
        using var recorder = new Recorder();
        var handler = new ScriptedHandler(Status(HttpStatusCode.TooManyRequests), Status(HttpStatusCode.TooManyRequests), Status(HttpStatusCode.OK, "[]"));

        var result = await RunWithPolicy("Users", handler, client => client.Users(new[] { "1" }));

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(recorder.For("Users"), Is.EqualTo(new[]
        {
            new Measurement("Users", "profile.svc.halowaypoint.com", 429, null),
            new Measurement("Users", "profile.svc.halowaypoint.com", 429, null),
            new Measurement("Users", "profile.svc.halowaypoint.com", 200, null),
        }));
    }

    [Test]
    public async Task AuthRetryIsCounted()
    {
        using var recorder = new Recorder();
        var handler = new ScriptedHandler(Status(HttpStatusCode.Unauthorized), Status(HttpStatusCode.OK));

        await RunWithPolicy("StatsGetChallengeDecks", handler, client => client.StatsGetChallengeDecks("xuid(1)"));

        Assert.That(recorder.For("StatsGetChallengeDecks").Select(m => m.Status), Is.EqualTo(new[] { 401, 200 }));
    }

    [Test]
    public async Task RequestWithNoResponseIsCountedAsStatusZeroWithTheError()
    {
        using var recorder = new Recorder();
        var handler = new ScriptedHandler(() => throw new HttpRequestException("connection reset"), Status(HttpStatusCode.OK));

        var result = await RunWithPolicy("Medals", handler, client => client.Medals());

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(recorder.For("Medals"), Is.EqualTo(new[]
        {
            new Measurement("Medals", "gamecms-hacs.svc.halowaypoint.com", 0, nameof(HttpRequestException)),
            new Measurement("Medals", "gamecms-hacs.svc.halowaypoint.com", 200, null),
        }));
    }
}
