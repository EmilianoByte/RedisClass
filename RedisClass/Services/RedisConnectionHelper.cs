using StackExchange.Redis;

namespace RedisClass.Services;

/// <summary>
/// Manages singleton Redis connection following StackExchange.Redis best practices.
/// ConnectionMultiplexer is thread-safe, expensive to create, and should be reused.
/// </summary>
public sealed class RedisConnectionHelper
{
    private static Lazy<ConnectionMultiplexer>? _lazyConnection;

    public static void Initialize(string connectionString)
    {
        _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            var options = ConfigurationOptions.Parse(connectionString);

            // Don't crash app if Redis is temporarily down.
            options.AbortOnConnectFail = false;

            // 5 seconds connection timeout.
            options.ConnectTimeout = 5000;

            // 5 seconds operation timeout.
            options.SyncTimeout = 5000;

            // Automatic retry on connection failure.
            options.ConnectRetry = 3;

            // TCP keepalive every 60 seconds to prevent idle disconnections.
            options.KeepAlive = 60;

            var connection = ConnectionMultiplexer.Connect(options);

            // Event handlers for monitoring and debugging.
            connection.ConnectionFailed += (sender, args) =>
            {
                Console.WriteLine($"[Redis] Connection failed: {args.Exception?.Message} | Endpoint: {args.EndPoint}");
            };

            connection.ConnectionRestored += (sender, args) =>
            {
                Console.WriteLine($"[Redis] Connection restored: {args.EndPoint}");
            };

            connection.ErrorMessage += (sender, args) =>
            {
                Console.WriteLine($"[Redis] Error: {args.Message} | Endpoint: {args.EndPoint}");
            };

            return connection;
        });
    }

    public static ConnectionMultiplexer Connection
    {
        get
        {
            if (_lazyConnection == null)
            {
                throw new InvalidOperationException(
                    "RedisConnectionHelper must be initialized before use. Call Initialize() first.");
            }
            return _lazyConnection.Value;
        }
    }

    /// <summary>
    /// Gets an IDatabase instance for Redis operations.
    /// IDatabase is lightweight and can be created per-request.
    /// </summary>
    public static IDatabase Database => Connection.GetDatabase();
}
