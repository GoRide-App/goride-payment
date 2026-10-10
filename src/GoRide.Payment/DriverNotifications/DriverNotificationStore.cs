using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Verification;
using MySqlConnector;

namespace GoRide.Payment.DriverNotifications;

public sealed class DriverNotificationStore(PaymentStore payments)
{
    // Shares the Paid/confirmation/receipt transaction. Unique trip and payment keys are
    // the final guard against redelivery; the snapshot never uses the original estimate.
    internal static async Task CreateForPaidTripAsync(MySqlConnection connection, MySqlTransaction transaction,
        PaymentRecord paid, VerificationRecord notice, CancellationToken ct)
    {
        var content = DriverNotificationRules.ForPaid(paid, notice);
        if (content is null) return;
        var notification = content.Notification;
        await using var command = new MySqlCommand("""
            INSERT INTO driver_payment_notifications
                (trip_id, payment_id, event_id, driver_id, amount_minor, currency, card_brand, card_last4, paid_at, next_attempt_at)
            VALUES (@trip, @payment, @event, @driver, @amount, @currency, @brand, @last4, @paid, @paid)
            ON DUPLICATE KEY UPDATE trip_id = trip_id
            """, connection, transaction);
        command.Parameters.AddWithValue("@trip", notification.TripId);
        command.Parameters.AddWithValue("@payment", content.PaymentId);
        command.Parameters.AddWithValue("@event", content.EventId);
        command.Parameters.AddWithValue("@driver", content.DriverId);
        command.Parameters.AddWithValue("@amount", decimal.ToInt64(notification.Amount * 100));
        command.Parameters.AddWithValue("@currency", notification.Currency);
        command.Parameters.AddWithValue("@brand", (object?)notification.CardBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("@last4", (object?)notification.CardLast4 ?? DBNull.Value);
        command.Parameters.AddWithValue("@paid", notification.PaidAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string Columns = "trip_id, amount_minor, currency, card_brand, card_last4, paid_at";

    public async Task<DriverNotificationPage> ListAsync(string driverId, DateTimeOffset since, long after, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand($"""
            SELECT {Columns}, id FROM driver_payment_notifications
            WHERE driver_id = @driver AND paid_at >= @since AND id > @after ORDER BY id LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("@driver", driverId);
        command.Parameters.AddWithValue("@since", since.UtcDateTime);
        command.Parameters.AddWithValue("@after", after);
        command.Parameters.AddWithValue("@limit", DriverNotificationRules.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<DriverNotification>();
        long last = 0;
        while (await reader.ReadAsync(ct))
        {
            if (items.Count == DriverNotificationRules.PageSize) return new(items, last);
            items.Add(Read(reader));
            last = reader.GetInt64(6);
        }
        return new(items, null);
    }

    public async Task<DriverNotification?> GetAsync(string driverId, string tripId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand($"""
            SELECT {Columns} FROM driver_payment_notifications WHERE driver_id = @driver AND trip_id = @trip
            """, connection);
        command.Parameters.AddWithValue("@driver", driverId);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<(DriverNotificationContent Content, int Attempts, string Token)?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString();
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using (var claim = new MySqlCommand("""
            UPDATE driver_payment_notifications
            SET status = 'Sending', lease_token = @token, lease_until = @lease, attempts = attempts + 1
            WHERE (status IN ('Pending', 'Retry') AND next_attempt_at <= @now)
               OR (status = 'Sending' AND lease_until < @now)
            ORDER BY next_attempt_at LIMIT 1
            """, connection))
        {
            claim.Parameters.AddWithValue("@token", token);
            claim.Parameters.AddWithValue("@lease", (now + DriverNotificationRules.Lease).UtcDateTime);
            claim.Parameters.AddWithValue("@now", now.UtcDateTime);
            if (await claim.ExecuteNonQueryAsync(ct) == 0) return null;
        }
        await using var read = new MySqlCommand($"""
            SELECT {Columns}, event_id, payment_id, driver_id, attempts
            FROM driver_payment_notifications WHERE lease_token = @token
            """, connection);
        read.Parameters.AddWithValue("@token", token);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (new(reader.GetString(6), reader.GetString(7), reader.GetString(8), Read(reader)), reader.GetInt32(9), token);
    }

    public async Task FinishAsync(string tripId, string token, string status, DateTimeOffset now,
        DateTimeOffset next, string? error, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            UPDATE driver_payment_notifications SET status = @status, accepted_at = @accepted,
                next_attempt_at = @next, last_error = @error, lease_token = NULL, lease_until = NULL
            WHERE trip_id = @trip AND lease_token = @token
            """, connection);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@accepted", status == "Accepted" ? now.UtcDateTime : DBNull.Value);
        command.Parameters.AddWithValue("@next", next.UtcDateTime);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@token", token);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DriverNotification Read(MySqlDataReader reader) => new(reader.GetString(0), reader.GetInt64(1) / 100m,
        reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
}
