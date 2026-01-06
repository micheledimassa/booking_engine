namespace flight_search.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string HostName { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    public string InventoryCommandExchange { get; set; } = "inventory.commands";
    public string InventoryEventExchange { get; set; } = "inventory.events";
    public string BookingEventExchange { get; set; } = "booking.events";
    public string FrappeEventExchange { get; set; } = "frappe.events";
}
