using FCG.MS.Payments.Application.DTOs;
using FCG.MS.Payments.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace FCG.MS.Payments.Infrastructure.Messaging;

public class PaymentConsumerService : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PaymentConsumerService> _logger;

    public PaymentConsumerService(IConfiguration config, IServiceProvider serviceProvider, ILogger<PaymentConsumerService> logger)
    {
        _factory = new ConnectionFactory
        {
            Uri = new Uri(config["RabbitMQ:Connection"])
        };
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentConsumerService is starting.");
        await using var connection = await _factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            queue: "payments_queue-v2",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (sender, ea) =>
        {
            try
            {
                _logger.LogInformation("Received message from payments_queue-v2");
                var message = Encoding.UTF8.GetString(ea.Body.ToArray());

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var createCheckoutRequest = JsonSerializer.Deserialize<CreateCustomerRequest>(message, jsonOptions);
                _logger.LogInformation("Deserialized message: {@CreateCheckoutRequest}", createCheckoutRequest);

                using var scope = _serviceProvider.CreateScope();
                var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();

                if (createCheckoutRequest != null)
                    await paymentService.CreateCustomerAsync(createCheckoutRequest);

                _logger.LogInformation("Processed message successfully");
                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message: {Message}", ex.Message);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
            }
        };

        await channel.BasicConsumeAsync(
            queue: "payments_queue-v2",
            autoAck: false,
            consumer: consumer);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }

    }
}
