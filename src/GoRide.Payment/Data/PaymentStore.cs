using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using MySqlConnector;

namespace GoRide.Payment.Data;

public sealed class PaymentStore(IConfiguration configuration)
{
    // MySQL's JSON numbers use floating-point storage. Store decimal text privately
    // so currency stays exact; the HTTP serializer still returns JSON numbers.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString
    };

    public MySqlConnection CreateConnection() => new(configuration.GetConnectionString("Payments")
        ?? throw new InvalidOperationException("ConnectionStrings:Payments is required."));

    public async Task<PaymentRecord?> GetAsync(string tripId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        return await ReadAsync(connection, null, tripId, ct);
    }

    public async Task<PaymentRecord> SelectCardAsync(string tripId, string riderId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var payment = await ReadAsync(connection, transaction, tripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip and final fare are not available yet.");
        var selected = PaymentRules.SelectCard(payment, riderId);
        if (selected != payment) await WriteAsync(connection, transaction, selected, ct);
        await transaction.CommitAsync(ct);
        return selected;
    }

    public async Task<PaymentRecord> CompleteAsync(TripCompletedEvent evt, CancellationToken ct)
    {
        PaymentRules.ValidateCompletion(evt);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evt, Json)));
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // The trip PK serializes concurrent first deliveries as well as selection/fare updates.
        var initial = PaymentRules.ApplyCompletion(null, evt);
        await using (var insert = new MySqlCommand("""
            INSERT INTO payments (trip_id, document) VALUES (@trip, @document)
            ON DUPLICATE KEY UPDATE trip_id = trip_id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@trip", evt.TripId);
            insert.Parameters.AddWithValue("@document", JsonSerializer.Serialize(initial, Json));
            await insert.ExecuteNonQueryAsync(ct);
        }
        var payment = (await ReadAsync(connection, transaction, evt.TripId!, ct))!;
        // Inbox and payment are committed together. Broker redelivery after a crash is safe.
        await using (var receipt = new MySqlCommand("""
            INSERT INTO processed_payment_events (event_id, trip_id, payload_hash) VALUES (@event, @trip, @hash)
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("@event", evt.EventId);
            receipt.Parameters.AddWithValue("@trip", evt.TripId);
            receipt.Parameters.AddWithValue("@hash", hash);
            try { await receipt.ExecuteNonQueryAsync(ct); }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                await transaction.RollbackAsync(ct);
                await using var existing = new MySqlCommand(
                    "SELECT trip_id, payload_hash FROM processed_payment_events WHERE event_id = @event", connection);
                existing.Parameters.AddWithValue("@event", evt.EventId);
                await using (var reader = await existing.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct) || reader.GetString(0) != evt.TripId || reader.GetString(1) != hash)
                        throw new PaymentException(409, "EVENT_ID_CONFLICT", "eventId has already been used with different data.");
                }
                return (await ReadAsync(connection, null, evt.TripId!, ct))!;
            }
        }
        payment = PaymentRules.ApplyCompletion(payment, evt);
        await WriteAsync(connection, transaction, payment, ct);
        await transaction.CommitAsync(ct);
        return payment;
    }

    private static async Task<PaymentRecord?> ReadAsync(MySqlConnection connection, MySqlTransaction? transaction,
        string tripId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "SELECT document FROM payments WHERE trip_id = @trip" + (transaction is null ? "" : " FOR UPDATE"),
            connection, transaction);
        command.Parameters.AddWithValue("@trip", tripId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<PaymentRecord>(json, Json);
    }

    private static async Task WriteAsync(MySqlConnection connection, MySqlTransaction transaction,
        PaymentRecord payment, CancellationToken ct)
    {
        await using var command = new MySqlCommand("UPDATE payments SET document = @document WHERE trip_id = @trip", connection, transaction);
        command.Parameters.AddWithValue("@trip", payment.TripId);
        command.Parameters.AddWithValue("@document", JsonSerializer.Serialize(payment, Json));
        await command.ExecuteNonQueryAsync(ct);
    }
}
