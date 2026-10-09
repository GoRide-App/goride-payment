using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Verification;
using MySqlConnector;

namespace GoRide.Payment.Receipts;

// Parameterised ADO.NET access for SCRUM-105 email receipts.
public sealed class ReceiptStore(PaymentStore payments)
{
    // Keeps the latest verified email for the trip; called when the rider opens checkout.
    public async Task SaveContactAsync(string tripId, string riderId, string email, string? displayName,
        DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            INSERT INTO payment_contacts (trip_id, rider_id, email, display_name, captured_at)
            VALUES (@trip, @rider, @email, @name, @at)
            ON DUPLICATE KEY UPDATE email = VALUES(email), display_name = VALUES(display_name), captured_at = VALUES(captured_at)
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@rider", riderId);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@name", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", at.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    // Called inside the transaction that marks the trip paid, so exactly one receipt exists
    // per paid trip no matter how often PayHere redelivers the notice.
    internal static async Task CreateForPaidTripAsync(MySqlConnection connection, MySqlTransaction transaction,
        PaymentRecord paid, VerificationRecord notice, CancellationToken ct)
    {
        string? email = null, name = null;
        await using (var read = new MySqlCommand(
            "SELECT email, display_name FROM payment_contacts WHERE trip_id = @trip", connection, transaction))
        {
            read.Parameters.AddWithValue("@trip", paid.TripId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                email = reader.GetString(0);
                name = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        var deliverable = ReceiptRules.IsDeliverableEmail(email);
        await using var insert = new MySqlCommand("""
            INSERT INTO payment_receipts (trip_id, receipt_id, recipient, recipient_name, status, next_attempt_at, created_at)
            VALUES (@trip, @receipt, @email, @name, @status, @now, @now)
            """, connection, transaction);
        insert.Parameters.AddWithValue("@trip", paid.TripId);
        insert.Parameters.AddWithValue("@receipt", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("@email", deliverable ? email : DBNull.Value);
        insert.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        insert.Parameters.AddWithValue("@status", deliverable ? ReceiptStatus.Pending : ReceiptStatus.NoEmail);
        insert.Parameters.AddWithValue("@now", notice.ReceivedAt.UtcDateTime);
        await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReceiptRow?> GetAsync(string tripId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT receipt_id, recipient, status, attempts, resend_count, sent_at, last_requested_at, provider, last_error
            FROM payment_receipts WHERE trip_id = @trip
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(tripId, Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!,
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
            Utc(reader, 5), Utc(reader, 6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    // Queues another delivery of a finished receipt. The conditions repeat the service checks so
    // two concurrent requests cannot both pass: the update is a no-op while a delivery is queued
    // or in flight, after the resend limit, or inside the cooldown.
    public async Task<bool> RequestResendAsync(string tripId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = (now - ReceiptRules.ResendCooldown).UtcDateTime;
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            UPDATE payment_receipts
            SET status = 'Pending', attempts = 0, next_attempt_at = @now, last_error = NULL,
                resend_count = resend_count + 1, last_requested_at = @now
            WHERE trip_id = @trip AND status IN ('Sent', 'Failed') AND recipient IS NOT NULL
              AND resend_count < @max
              AND (last_requested_at IS NULL OR last_requested_at <= @cutoff)
              AND (sent_at IS NULL OR sent_at <= @cutoff)
            """, connection);
        command.Parameters.AddWithValue("@now", now.UtcDateTime);
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@max", ReceiptRules.MaxResends);
        command.Parameters.AddWithValue("@cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static DateTimeOffset? Utc(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    // Claims one due receipt with a lease, so concurrent senders never take the same row.
    // A sender that crashed mid-send leaves an expired lease, which makes the row due again.
    public async Task<(ReceiptContent Content, int Attempts, string Token)?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString();
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using (var claim = new MySqlCommand("""
            UPDATE payment_receipts
            SET status = 'Sending', lease_token = @token, lease_until = @lease, attempts = attempts + 1
            WHERE (status IN ('Pending', 'Retry') AND next_attempt_at <= @now) OR (status = 'Sending' AND lease_until < @now)
            ORDER BY next_attempt_at
            LIMIT 1
            """, connection))
        {
            claim.Parameters.AddWithValue("@token", token);
            claim.Parameters.AddWithValue("@lease", (now + ReceiptRules.Lease).UtcDateTime);
            claim.Parameters.AddWithValue("@now", now.UtcDateTime);
            if (await claim.ExecuteNonQueryAsync(ct) == 0) return null;
        }

        string tripId, receiptId, recipient, currency, reference;
        string? name, brand, card;
        long amount;
        int attempts;
        DateTime paidAt;
        await using (var read = new MySqlCommand("""
            SELECT r.trip_id, r.receipt_id, r.recipient, r.recipient_name, r.attempts,
                   c.amount_minor, c.currency, c.payment_method, c.card_masked, c.provider_payment_id, c.paid_at
            FROM payment_receipts r JOIN payment_confirmations c ON c.trip_id = r.trip_id
            WHERE r.lease_token = @token
            """, connection))
        {
            read.Parameters.AddWithValue("@token", token);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            tripId = reader.GetString(0);
            receiptId = Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)!;
            recipient = reader.GetString(2);
            name = reader.IsDBNull(3) ? null : reader.GetString(3);
            attempts = reader.GetInt32(4);
            amount = reader.GetInt64(5);
            currency = reader.GetString(6);
            brand = reader.IsDBNull(7) ? null : reader.GetString(7);
            card = reader.IsDBNull(8) ? null : reader.GetString(8);
            reference = reader.GetString(9);
            paidAt = reader.GetDateTime(10);
        }
        var payment = await PaymentStore.ReadAsync(connection, null, tripId, ct);
        var last4 = card is { Length: >= 4 } && card[^4..].All(char.IsAsciiDigit) ? card[^4..] : null;
        return (new ReceiptContent(receiptId, tripId, recipient, name, amount, currency, brand, last4, reference,
            new DateTimeOffset(DateTime.SpecifyKind(paidAt, DateTimeKind.Utc)), payment?.Breakdown), attempts, token);
    }

    public async Task MarkSentAsync(string tripId, string token, string provider, string messageId, DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            UPDATE payment_receipts
            SET status = 'Sent', sent_at = @at, provider = @provider, provider_message_id = @message,
                last_error = NULL, lease_token = NULL, lease_until = NULL
            WHERE trip_id = @trip AND lease_token = @token
            """, connection);
        command.Parameters.AddWithValue("@at", at.UtcDateTime);
        command.Parameters.AddWithValue("@provider", provider);
        command.Parameters.AddWithValue("@message", messageId.Length <= 128 ? messageId : messageId[..128]);
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@token", token);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(string tripId, string token, string status, DateTimeOffset nextAttempt,
        string error, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            UPDATE payment_receipts
            SET status = @status, next_attempt_at = @next, last_error = @error, lease_token = NULL, lease_until = NULL
            WHERE trip_id = @trip AND lease_token = @token
            """, connection);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@next", nextAttempt.UtcDateTime);
        command.Parameters.AddWithValue("@error", ReceiptRules.ErrorSummary(error));
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@token", token);
        await command.ExecuteNonQueryAsync(ct);
    }
}
