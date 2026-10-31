using Microsoft.Extensions.Options;
using Surprenant.Grunt.Core.Storage;
using Surprenant.Grunt.Models;
using System.Text.Json;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>
/// <see cref="IOAuthStorage"/> (Grunt's token storage abstraction, used by HaloInfiniteClientFactory) plus clearing. Pluggable:
/// register another implementation (database row, Kubernetes secret, ...) before the gateway services to replace the file store.
/// </summary>
public interface IOAuthTokenStore : IOAuthStorage
{
    /// <summary>Deletes the stored token, forcing an admin sign-in.</summary>
    Task ClearAsync();
}

/// <summary>
/// File-backed token store (default <c>Gateway:Auth:TokenPath</c>, on a persistent volume in Kubernetes). Writes go to a temp file
/// and are moved into place, so a crash never leaves half a token. Single writer: run one gateway replica per volume.
/// </summary>
public sealed class FileOAuthTokenStore : IOAuthTokenStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileOAuthTokenStore(IOptions<GatewayOptions> options) : this(options.Value.Auth.TokenPath)
    {
    }

    public FileOAuthTokenStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public async Task<OAuthToken?> GetToken()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_path))
                return null;
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<OAuthToken>(stream);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetToken(OAuthToken token)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(token));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Exclusive lock file next to the token file: a second gateway instance on the same volume fails at startup instead of racing the
/// first one on refresh-token rotation.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock Acquire(string tokenPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tokenPath))!;
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, ".gateway.lock");
        try
        {
            return new InstanceLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another gateway instance holds {lockPath}. The file token store supports one replica; scale to 1 or use a shared store.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
