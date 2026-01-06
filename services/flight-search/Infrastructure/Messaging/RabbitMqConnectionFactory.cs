using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace flight_search.Infrastructure.Messaging;

public sealed class RabbitMqConnectionFactory : IRabbitMqConnectionFactory
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqConnectionFactory> _logger;
    private readonly object _syncRoot = new();
    private IConnection? _connection;
    private bool _disposed;

    private const int MaxAttempts = 10;

    public RabbitMqConnectionFactory(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnectionFactory> logger)
    {
        _logger = logger;
        var cfg = options.Value;
        _factory = new ConnectionFactory
        {
            HostName = cfg.HostName,
            Port = cfg.Port,
            UserName = cfg.UserName,
            Password = cfg.Password,
            VirtualHost = cfg.VirtualHost,
            DispatchConsumersAsync = true,
            ClientProvidedName = $"flight-search-{Environment.MachineName}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
    }

    public IConnection GetConnection()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RabbitMqConnectionFactory));

        if (_connection?.IsOpen == true)
            return _connection;

        lock (_syncRoot)
        {
            if (_connection?.IsOpen == true)
                return _connection;

            _connection?.Dispose();
            _connection = CreateConnectionWithRetry();
            return _connection;
        }
    }

    private IConnection CreateConnectionWithRetry()
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var connection = _factory.CreateConnection();
                if (attempt > 1)
                {
                    _logger.LogInformation("Connessione RabbitMQ stabilita dopo {Attempts} tentativi", attempt);
                }
                return connection;
            }
            catch (BrokerUnreachableException ex)
            {
                lastException = ex;
                WaitBeforeRetry(attempt, ex);
            }
            catch (SocketException ex)
            {
                lastException = ex;
                WaitBeforeRetry(attempt, ex);
            }
        }

        throw new InvalidOperationException("Impossibile stabilire una connessione RabbitMQ dopo molteplici tentativi", lastException);
    }

    private void WaitBeforeRetry(int attempt, Exception ex)
    {
        var delaySeconds = Math.Min(5 * attempt, 30);
        _logger.LogWarning(ex, "Tentativo {Attempt}/{Max} di connessione a RabbitMQ fallito. Retry tra {Delay}s", attempt, MaxAttempts, delaySeconds);
        Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _connection?.Dispose();

        _disposed = true;
    }
}
