using flight_search.Infrastructure.Inbox;
using flight_search.Infrastructure.Messaging;
using flight_search.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace flight_search.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(options =>
        {
            options.HostName = configuration["RABBITMQ_HOST"] ?? options.HostName;
            options.Port = configuration.GetValue("RABBITMQ_PORT", options.Port);
            options.UserName = configuration["RABBITMQ_USERNAME"] ?? options.UserName;
            options.Password = configuration["RABBITMQ_PASSWORD"] ?? options.Password;
            options.VirtualHost = configuration["RABBITMQ_VHOST"] ?? options.VirtualHost;
        });

        services.AddSingleton<IRabbitMqConnectionFactory, RabbitMqConnectionFactory>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();
        services.AddSingleton<IProcessedMessageStore, ProcessedMessageStore>();
        services.AddHostedService<RabbitMqTopologyHostedService>();
        services.AddHostedService<OutboxPublisherHostedService>();
        return services;
    }
}
