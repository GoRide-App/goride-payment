using System.Data;
using System.Text.Json;
using GoRide.Payment.Data;
using MySqlConnector;

namespace GoRide.Payment.Cards;

// A request survives disconnects/restarts, including its safe demo-card snapshot. No PAN or CVC.
public sealed record CardChargeRequest(string TripId, string RequestId, string CardId, string Brand, string Last4,
    string Behaviour, string OrderId, long AmountMinor, DateTimeOffset CreatedAt,
    string State = "Processing", int Attempts = 0, string? LastFailureCode = null);

public sealed record CardAttempt(int Number, int RequestAttempt, DateTimeOffset StartedAt);
public sealed record CardAttemptStatus(int AttemptCount, string? LastFailureCode, string? RequestId,
    string? RequestState, int RequestAttempts, bool AutoRetried, bool Retryable);

public sealed class CardAttemptStore(PaymentStore payments)
{
    public async Task<CardChargeRequest?> GetAsync(string tripId, string? requestId, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT document FROM payment_card_requests WHERE trip_id = @trip
            """ + (requestId is null ? " ORDER BY id DESC LIMIT 1" : " AND request_id = @request"), connection);
        command.Parameters.AddWithValue("@trip", tripId);
        command.Parameters.AddWithValue("@request", requestId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<CardChargeRequest>(json);
    }

    // Called under the trip lease. Checkout and request are created together.
    public async Task CreateAsync(CardChargeRequest request, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new MySqlCommand("""
            INSERT INTO payment_card_requests (trip_id, request_id, document) VALUES (@trip, @request, @document);
            INSERT INTO payment_checkouts (trip_id, idempotency_key, amount_minor, currency, success_url, cancel_url, created_at)
            VALUES (@trip, @order, @amount, 'LKR', '', '', @created);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@trip", request.TripId);
            command.Parameters.AddWithValue("@request", request.RequestId);
            command.Parameters.AddWithValue("@document", JsonSerializer.Serialize(request));
            command.Parameters.AddWithValue("@order", request.OrderId);
            command.Parameters.AddWithValue("@amount", request.AmountMinor);
            command.Parameters.AddWithValue("@created", request.CreatedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    // Starting an attempt and incrementing the payment's lifetime count commit together.
    // An interrupted attempt resumes with the same number and provider identity.
    public async Task<CardAttempt> StartAsync(CardChargeRequest request, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var payment = (await PaymentStore.ReadAsync(connection, transaction, request.TripId, ct))!;
        var index = request.Attempts + 1;
        await using (var existing = new MySqlCommand("""
            SELECT attempt_number, started_at FROM payment_card_attempts
            WHERE trip_id = @trip AND request_id = @request AND request_attempt = @index
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("@trip", request.TripId);
            existing.Parameters.AddWithValue("@request", request.RequestId);
            existing.Parameters.AddWithValue("@index", index);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new(reader.GetInt32(0), index, new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)));
        }
        var number = payment.CardAttemptCount + 1;
        await using (var insert = new MySqlCommand("""
            INSERT INTO payment_card_attempts (trip_id, request_id, attempt_number, request_attempt,
                outcome, amount_minor, currency, started_at, automatic)
            VALUES (@trip, @request, @number, @index, 'Processing', @amount, 'LKR', @started, @automatic)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@trip", request.TripId);
            insert.Parameters.AddWithValue("@request", request.RequestId);
            insert.Parameters.AddWithValue("@number", number);
            insert.Parameters.AddWithValue("@index", index);
            insert.Parameters.AddWithValue("@amount", request.AmountMinor);
            insert.Parameters.AddWithValue("@started", now.UtcDateTime);
            insert.Parameters.AddWithValue("@automatic", index > 1);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await PaymentStore.WriteAsync(connection, transaction, payment with { CardAttemptCount = number, Method = "Card" }, ct);
        await transaction.CommitAsync(ct);
        return new(number, index, now);
    }

    // Joins the verification transaction: there is no Paid record without its successful attempt.
    public static async Task CompleteAsync(MySqlConnection connection, MySqlTransaction transaction,
        CardChargeRequest request, CardAttempt attempt, string? code, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            UPDATE payment_card_attempts SET outcome = @outcome, failure_code = @code, completed_at = @completed
            WHERE trip_id = @trip AND request_id = @request AND request_attempt = @index;
            UPDATE payment_card_requests SET document = @document WHERE trip_id = @trip AND request_id = @request;
            """, connection, transaction);
        command.Parameters.AddWithValue("@outcome", code is null ? "Paid" : "Failed");
        command.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", now.UtcDateTime);
        command.Parameters.AddWithValue("@trip", request.TripId);
        command.Parameters.AddWithValue("@request", request.RequestId);
        command.Parameters.AddWithValue("@index", attempt.RequestAttempt);
        command.Parameters.AddWithValue("@document", JsonSerializer.Serialize(request));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CardAttemptStatus> StatusAsync(string tripId, CancellationToken ct)
    {
        // One snapshot prevents a status poll combining a new request with old attempt counts.
        await using var connection = payments.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM payment_card_attempts WHERE trip_id = @trip),
                (SELECT failure_code FROM payment_card_attempts WHERE trip_id = @trip AND failure_code IS NOT NULL
                    ORDER BY attempt_number DESC LIMIT 1),
                (SELECT document FROM payment_card_requests WHERE trip_id = @trip ORDER BY id DESC LIMIT 1)
            """, connection);
        command.Parameters.AddWithValue("@trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var request = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<CardChargeRequest>(reader.GetString(2));
        return new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), request?.RequestId,
            request?.State, request?.Attempts ?? 0, request?.Attempts > 1 || request?.State == "Retrying",
            request?.State == "Failed" && CardRetryRules.IsTransient(request.LastFailureCode));
    }
}
