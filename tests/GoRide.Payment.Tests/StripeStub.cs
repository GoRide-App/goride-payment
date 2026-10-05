using System.Net;
using System.Net.Http.Json;
using GoRide.Payment.Checkout;

namespace GoRide.Payment.Tests;

internal sealed class StripeStub : HttpMessageHandler
{
    private readonly Dictionary<string, HostedSession> sessions = [];
    public Dictionary<string, string>? LastCreate { get; private set; }
    public bool LoseNextCreateResponse { get; set; }
    public bool FailExpiration { get; set; }
    public bool FailAll { get; set; }
    public bool UnsafeUrl { get; set; }
    public int CreateRequests { get; private set; }
    public int Expirations { get; private set; }
    public int Count { get { lock (sessions) return sessions.Count; } }

    public void SetStatus(string status)
    {
        lock (sessions)
            foreach (var key in sessions.Keys.ToArray()) sessions[key] = sessions[key] with
            { Status = status, PaymentStatus = status == "complete" ? "paid" : "unpaid", Url = status == "open" ? sessions[key].Url : null };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (sessions)
        {
            if (FailAll) return new(HttpStatusCode.ServiceUnavailable);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/checkout/sessions")
            {
                CreateRequests++;
                var fields = body.Split('&').Select(part => part.Split('=', 2))
                    .ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));
                LastCreate = fields;
                var key = request.Headers.GetValues("Idempotency-Key").Single();
                if (!sessions.TryGetValue(key, out var session))
                {
                    var id = "cs_test_" + Guid.NewGuid().ToString("N");
                    session = new(id, "open", "unpaid", UnsafeUrl ? "https://evil.test/steal" : "https://checkout.stripe.com/c/pay/" + id,
                        long.Parse(fields["line_items[0][price_data][unit_amount]"]), fields["line_items[0][price_data][currency]"], false,
                        fields["client_reference_id"], DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());
                    sessions.Add(key, session);
                }
                if (LoseNextCreateResponse) { LoseNextCreateResponse = false; throw new HttpRequestException("Response was lost after creation."); }
                return Response(session);
            }
            var existing = sessions.Single(p => request.RequestUri!.AbsolutePath.Contains(p.Value.Id, StringComparison.Ordinal));
            if (request.Method == HttpMethod.Post)
            {
                if (FailExpiration) return new(HttpStatusCode.ServiceUnavailable);
                Expirations++;
                sessions[existing.Key] = existing.Value with { Status = "expired", Url = null };
            }
            return Response(sessions[existing.Key]);
        }
    }

    private static HttpResponseMessage Response(HostedSession session) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id = session.Id,
            status = session.Status,
            payment_status = session.PaymentStatus,
            url = session.Url,
            amount_total = session.AmountTotal,
            currency = session.Currency,
            livemode = session.LiveMode,
            client_reference_id = session.ClientReferenceId,
            expires_at = session.ExpiresAt
        })
    };

    // A shared fake provider represents Stripe across application restarts.
    protected override void Dispose(bool disposing) { }
}
