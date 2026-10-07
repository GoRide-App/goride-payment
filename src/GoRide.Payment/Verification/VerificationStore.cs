using System.Data;
using GoRide.Payment.Checkout;
using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Services;
using MySqlConnector;

namespace GoRide.Payment.Verification;

// Parameterised ADO.NET access for provider verification. The notice and the payment
// change commit together, so a crash or redelivery can never mark a trip paid twice
// or leave a recorded notice without its effect.
public sealed class VerificationStore(PaymentStore payments)
{
    public async Task<CheckoutAttempt?> FindAttemptAsync(string orderId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT trip_id, amount_minor, currency, success_url, cancel_url, created_at
            FROM payment_checkouts WHERE idempotency_key = @order
            """, connection);
        command.Parameters.AddWithValue("@order", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), orderId, reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
    }

    // Outcomes recorded for a trip, oldest first. Checkout uses them to hold back a new
    // order while a payment is pending or a received payment still needs reconciliation.
    public async Task<IReadOnlyList<(string OrderId, string Outcome)>> OutcomesAsync(string tripId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT order_id, outcome FROM payment_verifications WHERE trip_id = @trip ORDER BY id", connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var outcomes = new List<(string, string)>();
        while (await reader.ReadAsync(ct)) outcomes.Add((reader.GetString(0), reader.GetString(1)));
        return outcomes;
    }

    public async Task<VerificationResult> RecordAsync(VerificationRecord notice,
        Func<PaymentRecord, VerificationDecision> decide, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var payment = await PaymentStore.ReadAsync(connection, transaction, notice.TripId, ct)
            ?? throw new PaymentException(409, "TRIP_NOT_COMPLETED", "The completed trip for this order is not available.");

        var existing = await FindNoticeAsync(connection, transaction, notice, ct);
        if (existing is not null)
        {
            await transaction.RollbackAsync(ct);
            return Duplicate(existing.Value, notice, payment);
        }

        var decision = decide(payment);
        await using (var insert = new MySqlCommand("""
            INSERT INTO payment_verifications (provider, provider_payment_id, status_code, order_id, trip_id,
                amount_minor, currency, outcome, payload_hash, payment_method, card_masked, received_at)
            VALUES (@provider, @payment, @status, @order, @trip, @amount, @currency, @outcome, @hash, @method, @card, @received)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@provider", notice.Provider);
            insert.Parameters.AddWithValue("@payment", notice.PaymentId);
            insert.Parameters.AddWithValue("@status", notice.StatusCode);
            insert.Parameters.AddWithValue("@order", notice.OrderId);
            insert.Parameters.AddWithValue("@trip", notice.TripId);
            insert.Parameters.AddWithValue("@amount", notice.AmountMinor);
            insert.Parameters.AddWithValue("@currency", notice.Currency);
            insert.Parameters.AddWithValue("@outcome", decision.Outcome);
            insert.Parameters.AddWithValue("@hash", notice.PayloadHash);
            insert.Parameters.AddWithValue("@method", (object?)notice.PaymentMethod ?? DBNull.Value);
            insert.Parameters.AddWithValue("@card", (object?)notice.CardMasked ?? DBNull.Value);
            insert.Parameters.AddWithValue("@received", notice.ReceivedAt.UtcDateTime);
            try { await insert.ExecuteNonQueryAsync(ct); }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                // A concurrent delivery of the same notice committed first.
                await transaction.RollbackAsync(ct);
                await using var reread = payments.CreateConnection();
                await reread.OpenAsync(ct);
                var stored = await FindNoticeAsync(reread, null, notice, ct)
                    ?? throw new PaymentException(409, "NOTICE_CONFLICT", "The provider notice could not be recorded. Retry the notification.");
                return Duplicate(stored, notice, (await PaymentStore.ReadAsync(reread, null, notice.TripId, ct))!);
            }
        }
        if (decision.Payment != payment) await PaymentStore.WriteAsync(connection, transaction, decision.Payment, ct);
        await transaction.CommitAsync(ct);
        return new(decision.Outcome, decision.Payment, false);
    }

    private static VerificationResult Duplicate((string Hash, string Outcome) stored, VerificationRecord notice, PaymentRecord payment)
    {
        if (stored.Hash != notice.PayloadHash)
            throw new PaymentException(409, "NOTICE_CONFLICT", "This provider payment and status were already received with different details.");
        return new(stored.Outcome, payment, true);
    }

    private static async Task<(string Hash, string Outcome)?> FindNoticeAsync(MySqlConnection connection,
        MySqlTransaction? transaction, VerificationRecord notice, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT payload_hash, outcome FROM payment_verifications
            WHERE provider = @provider AND provider_payment_id = @payment AND status_code = @status
            """ + (transaction is null ? "" : " FOR UPDATE"), connection, transaction);
        command.Parameters.AddWithValue("@provider", notice.Provider);
        command.Parameters.AddWithValue("@payment", notice.PaymentId);
        command.Parameters.AddWithValue("@status", notice.StatusCode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }
}
