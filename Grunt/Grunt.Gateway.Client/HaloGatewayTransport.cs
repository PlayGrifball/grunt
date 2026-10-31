using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;
using Surprenant.Grunt.Models;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Surprenant.Grunt.Gateway.Client;

/// <summary>
/// HTTP transport of <see cref="HaloGatewayClient"/>: sends the request with the API key, retries transport failures with backoff,
/// and rebuilds the <see cref="HaloApiResultContainer{T, THaloApiErrorContainer}"/> from the gateway's response.
/// </summary>
/// <remarks>
/// A response with <see cref="HaloGatewayProtocol.UpstreamStatusHeader"/> is a Halo result the gateway already retried, so it is
/// returned as is whatever its HTTP status. Without the header (gateway quota 429, gateway restarting, ingress errors) 429/5xx are
/// retried; anything else becomes a container whose <see cref="HaloApiErrorContainer.Code"/> is the gateway's HTTP status
/// (e.g. 401 for a bad API key). Network errors are rethrown after the last attempt, like <see cref="HaloInfiniteClient"/>.
/// </remarks>
public sealed class HaloGatewayTransport
{
    private readonly HttpClient _http;
    private readonly HaloGatewayClientOptions _options;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, Task> _delay;

    public HaloGatewayTransport(HttpClient http, HaloGatewayClientOptions options, ILogger? logger = null, Func<TimeSpan, Task>? delay = null)
    {
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? (d => Task.Delay(d));
        if (_http.BaseAddress is null && options.BaseAddress is not null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    private sealed class Envelope<T>
    {
        public T? Result { get; set; }
        public HaloApiErrorContainer? Error { get; set; }
    }

    /// <summary>Calls a JSON route.</summary>
    public Task<HaloApiResultContainer<T, HaloApiErrorContainer>> SendAsync<T>(HttpMethod method, string relativeUri, object? body, string operation)
        => SendCoreAsync(method, relativeUri, body, operation, ReadJsonAsync<T>);

    /// <summary>Calls a binary route (raw bytes on success).</summary>
    public Task<HaloApiResultContainer<byte[], HaloApiErrorContainer>> SendBinaryAsync(HttpMethod method, string relativeUri, object? body, string operation)
        => SendCoreAsync(method, relativeUri, body, operation, ReadBinaryAsync);

    private async Task<HaloApiResultContainer<T, HaloApiErrorContainer>> SendCoreAsync<T>(HttpMethod method, string relativeUri, object? body,
        string operation, Func<HttpResponseMessage, Task<HaloApiResultContainer<T, HaloApiErrorContainer>>> read)
    {
        var retry = _options.Retry ?? new HaloRetryOptions();
        var maxAttempts = Math.Max(1, retry.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, relativeUri);
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);
            }
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: HaloGatewayProtocol.JsonOptions);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
            catch (Exception ex) when (retry.RetryOnNetworkErrors && HaloRequestPolicy.IsTransientException(ex) && attempt < maxAttempts)
            {
                var wait = HaloRequestPolicy.ComputeDelay(retry, attempt, null);
                _logger.LogWarning(ex, "{Operation}: gateway unreachable on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}", operation, attempt, maxAttempts, wait);
                await _delay(wait).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var fromGateway = response.Headers.Contains(HaloGatewayProtocol.UpstreamStatusHeader);
                var status = (int)response.StatusCode;
                if (!fromGateway && retry.IsRetryableStatus(status) && attempt < maxAttempts)
                {
                    var retryAfter = RetryAfter(response);
                    if (retryAfter is null || retryAfter <= retry.MaxRetryAfter)
                    {
                        var wait = HaloRequestPolicy.ComputeDelay(retry, attempt, retryAfter);
                        _logger.LogWarning("{Operation}: gateway answered {Status} on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}", operation, status, attempt, maxAttempts, wait);
                        await _delay(wait).ConfigureAwait(false);
                        continue;
                    }
                }
                return await read(response).ConfigureAwait(false);
            }
        }
    }

    private static async Task<HaloApiResultContainer<T, HaloApiErrorContainer>> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        if (response.Headers.Contains(HaloGatewayProtocol.UpstreamStatusHeader) && IsJson(response))
        {
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope<T>>(stream, HaloGatewayProtocol.JsonOptions).ConfigureAwait(false);
            var error = envelope?.Error ?? new HaloApiErrorContainer { Code = UpstreamStatus(response) };
            return new HaloApiResultContainer<T, HaloApiErrorContainer>(envelope is null ? default! : envelope.Result!, error);
        }
        return await GatewayErrorAsync<T>(response).ConfigureAwait(false);
    }

    private static async Task<HaloApiResultContainer<byte[], HaloApiErrorContainer>> ReadBinaryAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode && response.Headers.Contains(HaloGatewayProtocol.UpstreamStatusHeader) && !IsJson(response))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return new HaloApiResultContainer<byte[], HaloApiErrorContainer>(bytes, new HaloApiErrorContainer { Code = UpstreamStatus(response) });
        }
        return await ReadJsonAsync<byte[]>(response).ConfigureAwait(false);
    }

    private static async Task<HaloApiResultContainer<T, HaloApiErrorContainer>> GatewayErrorAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new HaloApiResultContainer<T, HaloApiErrorContainer>(default!, new HaloApiErrorContainer
        {
            Code = (int)response.StatusCode,
            Message = string.IsNullOrEmpty(text) ? $"Gateway answered {(int)response.StatusCode} {response.ReasonPhrase}" : text,
            RetryAfter = RetryAfter(response),
        });
    }

    private static bool IsJson(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is { } mediaType && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static int UpstreamStatus(HttpResponseMessage response)
        => response.Headers.TryGetValues(HaloGatewayProtocol.UpstreamStatusHeader, out var values) &&
           int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : (int)response.StatusCode;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter is { } ra
            ? ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;
}
