using Azure.Core;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BlinkMark.Infrastructure.Redis;

/// <summary>
/// Supplies the shared Redis connection, authenticated with Microsoft Entra ID.
/// </summary>
/// <remarks>
/// The cache is provisioned with <c>disableAccessKeyAuthentication: true</c>, so there is no
/// access key to configure and nothing would work if there were. Authentication is an Entra
/// token acquired through the ambient managed identity: the username is the identity's object
/// id and the password is the token.
/// <para>
/// Tokens expire, so the connection is rebuilt on demand rather than held forever. Redis is
/// Basic C0 with no SLA and restarts without warning anyway (research.md R1), so reconnection
/// has to be routine rather than exceptional — which conveniently makes token expiry a
/// non-event too.
/// </para>
/// <para>
/// Every caller must tolerate <see langword="null"/>. FR-069 requires preview and commenting to
/// be unaffected when presence is unavailable, and that is only true if an unreachable cache
/// produces a quiet absence rather than an exception.
/// </para>
/// </remarks>
public sealed class RedisConnectionProvider : IAsyncDisposable
{
    private static readonly string[] RedisScope = ["https://redis.azure.com/.default"];

    private readonly RedisOptions _options;
    private readonly TokenCredential? _credential;
    private readonly ILogger<RedisConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnectionMultiplexer? _connection;
    private DateTimeOffset _tokenExpiresOn = DateTimeOffset.MinValue;

    public RedisConnectionProvider(
        IOptions<BlinkMarkOptions> options,
        TokenCredential? credential,
        ILogger<RedisConnectionProvider> logger)
    {
        _options = options.Value.Redis;
        _credential = credential;
        _logger = logger;
    }

    /// <summary>Whether a usable connection currently exists.</summary>
    public bool IsAvailable => _connection is { IsConnected: true };

    /// <summary>
    /// Returns a connected database, or <see langword="null"/> if the cache is unreachable.
    /// </summary>
    public async Task<IDatabase?> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return connection?.GetDatabase();
    }

    /// <summary>
    /// Returns a subscriber, or <see langword="null"/> if the cache is unreachable.
    /// </summary>
    public async Task<ISubscriber?> GetSubscriberAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return connection?.GetSubscriber();
    }

    private async Task<IConnectionMultiplexer?> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            return null;
        }

        var current = _connection;
        if (current is { IsConnected: true } && DateTimeOffset.UtcNow < _tokenExpiresOn)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _connection;
            if (current is { IsConnected: true } && DateTimeOffset.UtcNow < _tokenExpiresOn)
            {
                return current;
            }

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            var configuration = new ConfigurationOptions
            {
                EndPoints = { { _options.Host, _options.Port } },
                Ssl = _options.UseSsl,
                AbortOnConnectFail = false,
                ConnectTimeout = (int)_options.ConnectTimeout.TotalMilliseconds,
                ConnectRetry = 2,
            };

            if (_options.UseEntraAuthentication)
            {
                if (_credential is null)
                {
                    _logger.LogWarning("Entra authentication is enabled for Redis but no credential is configured.");
                    return null;
                }

                var token = await _credential
                    .GetTokenAsync(new TokenRequestContext(RedisScope), cancellationToken)
                    .ConfigureAwait(false);

                // The object id in the token is the Redis "user". The access policy assignment
                // in identity.bicep is what turns it into permissions.
                configuration.User = ExtractObjectId(token.Token);
                configuration.Password = token.Token;

                // Reconnect a minute before the token dies rather than discovering it mid-call.
                _tokenExpiresOn = token.ExpiresOn.AddMinutes(-1);
            }
            else
            {
                // Local development against the container in docker-compose, which has no auth.
                _tokenExpiresOn = DateTimeOffset.MaxValue;
            }

            _connection = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
            return _connection;
        }
        catch (Exception exception)
        {
            // Never rethrown. Redis backs presence, rate limiting, and a quota cache — none of
            // which may take down preview or commenting when the cache is having a bad day.
            _logger.LogWarning(exception, "Redis is unavailable. Presence degrades; nothing else is affected.");
            _connection = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ExtractObjectId(string accessToken)
    {
        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return string.Empty;
        }

        var payload = System.Buffers.Text.Base64Url.DecodeFromChars(segments[1]);
        using var document = System.Text.Json.JsonDocument.Parse(payload);

        if (document.RootElement.TryGetProperty("oid", out var oid))
        {
            return oid.GetString() ?? string.Empty;
        }

        return document.RootElement.TryGetProperty("sub", out var sub)
            ? sub.GetString() ?? string.Empty
            : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
    }
}
