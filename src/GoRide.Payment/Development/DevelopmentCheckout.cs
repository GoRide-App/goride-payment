using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using GoRide.Payment.Checkout;
using GoRide.Payment.Data;
using GoRide.Payment.Models;
using GoRide.Payment.Services;

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
        group.MapGet("/config", (PayHereSettings settings) =>
        {
            try { settings.Validate(); return Results.Ok(new { configured = true, message = "PayHere sandbox is ready." }); }
            catch (PaymentException ex) { return Results.Ok(new { configured = false, message = ex.Message }); }
        });
        group.MapPost("/trips", async (DevTripRequest request, TripCompletionService completion, PaymentStore payments, TimeProvider clock, CancellationToken ct) =>
        {
            if (request.FinalFare is null or <= 0 or > 999999.99m)
                throw new PaymentException(400, "INVALID_FARE", "Enter a test fare between LKR 0.01 and 999,999.99.");
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
            return Results.Ok(await payments.SelectCardAsync(trip, Rider, ct));
        });
        group.MapGet("/trips/{tripId}", async (string tripId, PaymentStore payments, CancellationToken ct) =>
        {
            var payment = await payments.GetAsync(tripId, ct);
            return payment?.RiderId == Rider ? Results.Ok(payment) : Results.NotFound();
        });
        group.MapPost("/trips/{tripId}/checkout", async (string tripId, Controllers.CheckoutRequest request, CheckoutService checkout, CancellationToken ct) =>
            Results.Ok(await checkout.CreateAsync(tripId, Rider, RiderContact.Sandbox, ct)));
    }

    private static IResult Asset(string name, string contentType) => Results.Stream(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("GoRide.Payment.Development." + name)!, contentType);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DevTripRequest(decimal? FinalFare);
