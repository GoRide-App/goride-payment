using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using GoRide.Payment.Services;

namespace GoRide.Payment.Checkout;

public sealed class StripeCheckoutClient(IHttpClientFactory clients, StripeSettings settings) : ICheckoutProvider
{
    public Task<HostedSession> CreateAsync(CheckoutAttempt attempt, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["payment_method_types[0]"] = "card",
            ["client_reference_id"] = attempt.TripId,
            ["success_url"] = attempt.SuccessUrl,
            ["cancel_url"] = attempt.CancelUrl,
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = attempt.Currency,
            ["line_items[0][price_data][unit_amount]"] = attempt.AmountMinor.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = "GoRide completed ride",
            ["metadata[trip_id]"] = attempt.TripId,
            ["metadata[checkout_key]"] = attempt.IdempotencyKey
        };
        return SendAsync(HttpMethod.Post, "checkout/sessions", attempt, fields, attempt.IdempotencyKey, ct);
    }

    public Task<HostedSession> GetAsync(CheckoutAttempt attempt, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "checkout/sessions/" + SessionId(attempt), attempt, null, null, ct);

    public Task<HostedSession> ExpireAsync(CheckoutAttempt attempt, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "checkout/sessions/" + SessionId(attempt) + "/expire", attempt, [], attempt.IdempotencyKey + "-expire", ct);

    private static string SessionId(CheckoutAttempt attempt)
    {
        if (attempt.SessionId is null || !attempt.SessionId.StartsWith("cs_test_", StringComparison.Ordinal)
            || attempt.SessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw InvalidResponse();
        return attempt.SessionId;
    }

    private async Task<HostedSession> SendAsync(HttpMethod method, string path, CheckoutAttempt attempt,
        IEnumerable<KeyValuePair<string, string>>? fields, string? key, CancellationToken ct)
    {
        settings.Validate();
        using var request = new HttpRequestMessage(method, new Uri("https://api.stripe.com/v1/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
        request.Headers.Add("Stripe-Version", "2025-02-24.acacia");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await clients.CreateClient("Stripe").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new PaymentException(503, "CHECKOUT_PROVIDER_UNAVAILABLE", "Stripe could not prepare checkout. Retry this trip; a new charge will not be started.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            var session = new HostedSession(
                root.GetProperty("id").GetString()!, root.GetProperty("status").GetString()!,
                root.GetProperty("payment_status").GetString()!,
                root.TryGetProperty("url", out var url) && url.ValueKind != JsonValueKind.Null ? url.GetString() : null,
                root.GetProperty("amount_total").GetInt64(), root.GetProperty("currency").GetString()!,
                root.GetProperty("livemode").GetBoolean(), root.GetProperty("client_reference_id").GetString()!,
                root.GetProperty("expires_at").GetInt64());
            ValidateSession(session, attempt);
            return session;
        }
        catch (Exception ex) when (ex is HttpRequestException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new PaymentException(503, "CHECKOUT_PROVIDER_UNAVAILABLE", "Stripe is temporarily unavailable. Retry this trip to recover the same checkout.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    public static void ValidateSession(HostedSession session, CheckoutAttempt attempt)
    {
        if (session.LiveMode || session.Id is null || !session.Id.StartsWith("cs_test_", StringComparison.Ordinal)
            || session.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            || (attempt.SessionId is not null && session.Id != attempt.SessionId)
            || session.AmountTotal != attempt.AmountMinor || session.Currency != attempt.Currency
            || session.ClientReferenceId != attempt.TripId
            || session.Status is not ("open" or "expired" or "complete")
            || session.PaymentStatus is not ("unpaid" or "paid" or "no_payment_required")
            || session.ExpiresAt <= 0 || session.ExpiresAt > 253402300799)
            throw InvalidResponse();
        if (session.Status == "open" && !IsSafeCheckoutUrl(session.Url)) throw InvalidResponse();
    }

    public static bool IsSafeCheckoutUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "checkout.stripe.com" && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && !url!.Any(char.IsControl);

    private static PaymentException InvalidResponse() => new(502, "INVALID_CHECKOUT_RESPONSE", "Stripe returned an unexpected checkout response. No redirect was issued.");
}
