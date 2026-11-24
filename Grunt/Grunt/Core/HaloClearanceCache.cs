using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// The account's current flight (clearance), which Halo wants in the <c>343-clearance</c> header of its Economy and catalog calls.
/// Fetched once per <c>lifetime</c> for every caller; when Halo cannot be asked the last good one is kept and asking is retried
/// after <see cref="RetryAfterFailure"/>.
/// </summary>
public sealed class HaloClearanceCache
{
    /// <summary>How long to leave Halo alone after a failed fetch.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly Func<Task<string?>> _fetch;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _value;
    private DateTimeOffset _validUntil;

    public HaloClearanceCache(Func<Task<string?>> fetch, TimeSpan lifetime, TimeProvider? time = null, ILogger? logger = null)
    {
        _fetch = fetch;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>The current clearance, or the last good one, or null when there has never been one.</summary>
    public async Task<string?> GetAsync()
    {
        if (IsFresh())
            return _value;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsFresh())
                return _value;

            string? fetched = null;
            try
            {
                fetched = await _fetch().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not fetch the account's clearance");
            }

            if (string.IsNullOrEmpty(fetched))
            {
                _validUntil = _time.GetUtcNow() + RetryAfterFailure;
                return _value;
            }

            _value = fetched;
            _validUntil = _time.GetUtcNow() + _lifetime;
            return _value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh() => _time.GetUtcNow() < _validUntil;
}
