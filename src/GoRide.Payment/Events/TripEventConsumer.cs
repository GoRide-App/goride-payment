using System.Text.Json;
using Confluent.Kafka;
using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Services;

namespace GoRide.Payment.Events;

public sealed class TripEventConsumer(IConfiguration configuration, IServiceScopeFactory scopes,
    ILogger<TripEventConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Kafka:BootstrapServers is required when Kafka is enabled."),
            GroupId = configuration["Kafka:GroupId"] ?? "goride-payment",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
        if (configuration["Kafka:SecurityProtocol"] is { } protocol)
            config.SecurityProtocol = Enum.Parse<SecurityProtocol>(protocol, true);
        if (configuration["Kafka:SaslMechanism"] is { } mechanism)
            config.SaslMechanism = Enum.Parse<SaslMechanism>(mechanism, true);
        config.SaslUsername = configuration["Kafka:SaslUsername"];
        config.SaslPassword = configuration["Kafka:SaslPassword"];
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(configuration["Kafka:TripEventsTopic"] ?? "goride.trip.events");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var record = consumer.Consume(stoppingToken);
                try
                {
                    if (record.Message?.Value is null) throw new JsonException("The event body is missing.");
                    using var json = JsonDocument.Parse(record.Message.Value);
                    if (json.RootElement.ValueKind != JsonValueKind.Object
                        || !json.RootElement.TryGetProperty("eventType", out var type)
                        || type.ValueKind != JsonValueKind.String)
                        throw new JsonException("The event must be an object with a string eventType.");
                    if (type.GetString() == "TRIP_COMPLETED")
                    {
                        var evt = json.Deserialize<TripCompletedEvent>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<PaymentStore>().CompleteAsync(evt, stoppingToken);
                    }
                    // Commit only after durable processing. A failed commit may replay the event safely.
                    consumer.Commit(record);
                }
                catch (Exception ex) when (ex is JsonException or PaymentException)
                {
                    logger.LogCritical(ex, "Invalid payment event at {Position}; offset was not committed. Correct or reconcile this event before restarting.", record.TopicPartitionOffset);
                    throw;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Payment event at {Position} failed; retrying without advancing the offset.", record.TopicPartitionOffset);
                    consumer.Seek(record.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { consumer.Close(); }
    }, stoppingToken);
}
