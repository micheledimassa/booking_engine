using RabbitMQ.Client;

namespace flight_booking.Infrastructure.Messaging;

public interface IRabbitMqConnectionFactory : IDisposable
{
    IConnection GetConnection();
}
