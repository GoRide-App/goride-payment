using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using GoRide.Payment.Checkout;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Receipts;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;

namespace GoRide.Payment.Development;

public static class DevelopmentCheckout
{
    private const string Rider = "dev-checkout-rider";

    public static void MapDevelopmentCheckout(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("DevelopmentCheckout:Enabled")) return;
        var group = app.MapGroup("/dev/payments").AllowAnonymous().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var address = http.Connection.RemoteIpAddress;
            if (address is null || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                || !Uri.TryCreate($"{http.Request.Scheme}://{http.Request.Host}", UriKind.Absolute, out var origin) || !origin.IsLoopback)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // The page may only post its signed checkout form to the PayHere sandbox.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action https://sandbox.payhere.lk";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (HttpMethods.IsPost(http.Request.Method))
            {
                var suppliedOrigin = http.Request.Headers.Origin.ToString();
                if (http.Request.Headers["X-GoRide-Dev"] != "1"
                    || (!string.IsNullOrEmpty(suppliedOrigin) && suppliedOrigin != origin.GetLeftPart(UriPartial.Authority)))
                    return Results.Json(new { status = 403, code = "DEV_ORIGIN_REJECTED", title = "Use the local development page to run this action." }, statusCode: 403);
            }
            return await next(context);
        });
        group.MapGet("", () => Asset("checkout.html", "text/html; charset=utf-8"));
        group.MapGet("/app.css", () => Asset("checkout.css", "text/css; charset=utf-8"));
        group.MapGet("/app.js", () => Asset("checkout.js", "text/javascript; charset=utf-8"));
        group.MapGet("/config", (PayHereSettings settings, EmailSettings email) =>
        {
            var receipts = email.UseBrevo
                ? $"Receipt emails are sent with Brevo from {email.FromAddress}."
                : "Receipt emails are logged locally. Set Email:Provider to Brevo to send real ones.";
            try { settings.Validate(); return Results.Ok(new { configured = true, message = "PayHere sandbox is ready.", receipts }); }
            catch (PaymentException ex) { return Results.Ok(new { configured = false, message = ex.Message, receipts }); }
        });
        group.MapPost("/trips", async (DevTripRequest request, TripCompletionService completion, PaymentStore payments,
            ReceiptStore receipts, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.FinalFare is null or <= 0 or > 999999.99m)
                throw new PaymentException(400, "INVALID_FARE", "Enter a test fare between LKR 0.01 and 999,999.99.");
            var email = string.IsNullOrEmpty(request.Email) ? null : request.Email;
            if (email is not null && !ReceiptRules.IsDeliverableEmail(email))
                throw new PaymentException(400, "INVALID_EMAIL", "Enter a valid email address, like you@example.com.");
            var trip = "dev-trip-" + Guid.NewGuid().ToString("N");
            await completion.CompleteAsync(new TripCompletedEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventType = "TRIP_COMPLETED",
                TripId = trip,
                RiderId = Rider,
                DriverId = "dev-checkout-driver",
                OccurredAt = clock.GetUtcNow(),
                Payload = new CompletedFare { FinalFare = request.FinalFare, EstimatedFare = request.FinalFare }
            }, ct);
            var selected = await payments.SelectCardAsync(trip, Rider, ct);
            // Stands in for the verified email the identity session gives a real rider at checkout.
            if (email is not null) await receipts.SaveContactAsync(trip, Rider, email, null, clock.GetUtcNow(), ct);
            return Results.Ok(selected);
        });
        group.MapGet("/trips/{tripId}", async (string tripId, PaymentStore payments, CancellationToken ct) =>
        {
            var payment = await payments.GetAsync(tripId, ct);
            return payment?.RiderId == Rider ? Results.Ok(payment) : Results.NotFound();
        });
        group.MapPost("/trips/{tripId}/checkout", async (string tripId, Controllers.CheckoutRequest request, CheckoutService checkout, CancellationToken ct) =>
            Results.Ok(await checkout.CreateAsync(tripId, Rider, RiderContact.Sandbox, ct)));
        // PayHere cannot call a localhost notify_url. This signs the notice PayHere would send
        // for the trip's open order and runs it through the real parsing and verification path.
        group.MapPost("/trips/{tripId}/simulate-notify", async (string tripId, DevNotifyRequest request, PaymentStore payments,
            CheckoutStore checkouts, PaymentVerificationService verification, PayHereSettings settings, CancellationToken ct) =>
        {
            if (request.StatusCode is not (2 or 0 or -1 or -2))
                throw new PaymentException(400, "INVALID_REQUEST", "statusCode must be 2, 0, -1 or -2.");
            var payment = await payments.GetAsync(tripId, ct);
            if (payment?.RiderId != Rider) return Results.NotFound();
            var attempt = await checkouts.LatestAsync(tripId, ct)
                ?? throw new PaymentException(409, "CHECKOUT_NOT_STARTED", "Open the checkout first so there is an order to notify about.");
            settings.Validate();
            var amount = PayHereSignature.FormatAmount(attempt.AmountMinor);
            var status = request.StatusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var paymentId = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["merchant_id"] = settings.MerchantId,
                ["order_id"] = attempt.OrderId,
                ["payment_id"] = paymentId,
                ["payhere_amount"] = amount,
                ["payhere_currency"] = attempt.Currency,
                ["status_code"] = status,
                ["md5sig"] = PayHereSignature.NotifySignature(settings.MerchantId, attempt.OrderId, amount, attempt.Currency, status, settings.MerchantSecret),
                ["method"] = "VISA",
                ["status_message"] = "Simulated sandbox notification",
                ["card_no"] = "************1292"
            });
            var result = await verification.VerifyAsync(PayHereNotice.From(form), ct);
            return Results.Ok(new { outcome = result.Outcome, payment = result.Payment });
        });
        // SCRUM-104: the same confirmation the rider app reads, for the fixture rider only.
        group.MapGet("/trips/{tripId}/confirmation", async (string tripId, ConfirmationService confirmations, CancellationToken ct) =>
            Results.Ok(await confirmations.GetAsync(tripId, Rider, ct)));
        group.MapPost("/trips/{tripId}/confirmation/acknowledge", async (string tripId, Controllers.AcknowledgeConfirmationRequest request,
            ConfirmationService confirmations, CancellationToken ct) =>
            Results.Ok(await confirmations.AcknowledgeAsync(tripId, Rider, request.ConfirmationId, ct)));
        // SCRUM-105: the receipt status and resend the rider app uses, plus a preview of the email.
        group.MapGet("/trips/{tripId}/receipt", async (string tripId, ReceiptService receipts, CancellationToken ct) =>
            Results.Ok(await receipts.GetAsync(tripId, Rider, ct)));
        group.MapPost("/trips/{tripId}/receipt/resend", async (string tripId, Controllers.ResendReceiptRequest request,
            ReceiptService receipts, CancellationToken ct) =>
            Results.Ok(await receipts.ResendAsync(tripId, Rider, ct)));
        group.MapGet("/trips/{tripId}/receipt/preview", async (string tripId, HttpContext http, ReceiptService service,
            ReceiptStore receipts, CancellationToken ct) =>
        {
            await service.GetAsync(tripId, Rider, ct);
            var content = await receipts.GetContentAsync(tripId, ct);
            if (content is null) return Results.NotFound();
            // The email uses inline styles; nothing in it may run script or load anything.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; sandbox";
            return Results.Content(ReceiptRenderer.Render(content).Html, "text/html; charset=utf-8");
        });
    }

    private static IResult Asset(string name, string contentType) => Results.Stream(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("GoRide.Payment.Development." + name)!, contentType);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DevTripRequest(decimal? FinalFare, string? Email = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DevNotifyRequest(int? StatusCode);
