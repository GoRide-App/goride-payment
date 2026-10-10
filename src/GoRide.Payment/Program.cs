using GoRide.Payment.Auth;
using GoRide.Payment.Checkout;
using GoRide.Payment.Confirmation;
using GoRide.Payment.Development;
using GoRide.Payment.Data;
using GoRide.Payment.Events;
using GoRide.Payment.Services;
using GoRide.Payment.Verification;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState)
    {
        Status = 400,
        Title = "The request is invalid.",
        Extensions = { ["code"] = "INVALID_REQUEST" }
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddScoped<PaymentStore>();
builder.Services.AddScoped<CheckoutStore>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<TripCompletionService>();
builder.Services.AddScoped<VerificationStore>();
builder.Services.AddScoped<PaymentVerificationService>();
builder.Services.AddScoped<ConfirmationStore>();
builder.Services.AddScoped<ConfirmationService>();
builder.Services.AddScoped<GoRide.Payment.Receipts.ReceiptStore>();
builder.Services.AddScoped<GoRide.Payment.Receipts.ReceiptService>();
// In-app demo cards (Stripe-like test cards) and the payment status shared with the driver.
builder.Services.AddScoped<GoRide.Payment.Cards.CardStore>();
builder.Services.AddScoped<GoRide.Payment.Cards.CardService>();
builder.Services.AddScoped<GoRide.Payment.Cards.CardPaymentService>();
builder.Services.AddScoped<GoRide.Payment.Cards.CardAttemptStore>();
builder.Services.AddScoped<PaymentStatusService>();
// Receipts use Brevo by default. Log is an explicit local-preview mode and is never reported as Sent.
builder.Services.AddSingleton<GoRide.Payment.Receipts.EmailSettings>();
builder.Services.AddHttpClient("Brevo", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
builder.Services.AddScoped<GoRide.Payment.Receipts.IEmailSender>(sp =>
    sp.GetRequiredService<GoRide.Payment.Receipts.EmailSettings>().Provider.ToLowerInvariant() switch
    {
        "brevo" => ActivatorUtilities.CreateInstance<GoRide.Payment.Receipts.BrevoEmailSender>(sp),
        "log" => ActivatorUtilities.CreateInstance<GoRide.Payment.Receipts.LogEmailSender>(sp),
        _ => throw new InvalidOperationException("Email:Provider must be Brevo or Log.")
    });
builder.Services.AddSingleton<GoRide.Payment.Receipts.ReceiptDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GoRide.Payment.Receipts.ReceiptDispatcher>());
builder.Services.AddScoped<GoRide.Payment.DriverNotifications.DriverNotificationStore>();
builder.Services.AddScoped<GoRide.Payment.DriverNotifications.IDriverNotificationSender, GoRide.Payment.DriverNotifications.DriverNotificationSender>();
builder.Services.AddHttpClient("DriverNotifications", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
builder.Services.AddSingleton<GoRide.Payment.DriverNotifications.DriverNotificationDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GoRide.Payment.DriverNotifications.DriverNotificationDispatcher>());
builder.Services.AddSingleton<PayHereSettings>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(IdentitySessionHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, IdentitySessionHandler>(IdentitySessionHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("Identity", client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Identity:BaseUrl"] ?? "https://localhost:7136").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyMethod().AllowAnyHeader().AllowCredentials()));
if (builder.Configuration.GetValue<bool>("Kafka:Enabled")) builder.Services.AddHostedService<TripEventConsumer>();

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var (status, code, message) = error switch
    {
        PaymentException p => (p.Status, p.Code, p.Message),
        // Minimal API binding failures (unknown fields, malformed JSON) in Development.
        BadHttpRequestException b => (b.StatusCode, "INVALID_REQUEST", "The request is invalid."),
        MySqlException => (503, "PAYMENT_STORE_UNAVAILABLE", "The payment store is unavailable. Please try again."),
        _ => (500, "INTERNAL_ERROR", "The request could not be completed.")
    };
    context.Response.StatusCode = status;
    var problem = new ProblemDetails
    {
        Status = status,
        Title = message,
        Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
    };
    if (error is PaymentException { RetryAfter: { } wait })
    {
        // Also in the body: browsers hide Retry-After from cross-origin scripts.
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        problem.Extensions["retryAfterSeconds"] = seconds;
    }
    if (error is PaymentException { Attempts: { } count } failure)
    {
        problem.Extensions["retryable"] = failure.Retryable;
        problem.Extensions["autoRetried"] = failure.AutoRetried;
        problem.Extensions["attempts"] = count;
    }
    await context.Response.WriteAsJsonAsync(problem);
}));
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDevelopmentCheckout();
app.MapGet("/health", async (PaymentStore store, CancellationToken ct) =>
{
    await using var connection = store.CreateConnection();
    await connection.OpenAsync(ct);
    await using var command = new MySqlCommand("""
        SELECT 1 FROM payments
        LEFT JOIN payment_checkouts ON payments.trip_id = payment_checkouts.trip_id
        LEFT JOIN payment_verifications ON payments.trip_id = payment_verifications.trip_id
        LEFT JOIN payment_confirmations ON payments.trip_id = payment_confirmations.trip_id
        LEFT JOIN payment_contacts ON payments.trip_id = payment_contacts.trip_id
        LEFT JOIN payment_receipts ON payments.trip_id = payment_receipts.trip_id
        LEFT JOIN driver_payment_notifications ON payments.trip_id = driver_payment_notifications.trip_id
        LEFT JOIN processed_payment_events ON payments.trip_id = processed_payment_events.trip_id
        LEFT JOIN payment_cards ON FALSE
        LEFT JOIN payment_card_requests ON FALSE
        LEFT JOIN payment_card_attempts ON FALSE LIMIT 1
        """, connection);
    await command.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "healthy", database = "connected" });
});
app.Run();

public partial class Program;
