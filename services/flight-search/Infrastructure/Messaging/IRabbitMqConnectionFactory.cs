using RabbitMQ.Client;

namespace flight_search.Infrastructure.Messaging;

public interface IRabbitMqConnectionFactory : IDisposable
{
    IConnection GetConnection();
}
