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
}
