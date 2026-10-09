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
        MySqlException => (503, "PAYMENT_STORE_UNAVAILABLE", "The payment store is unavailable. Please try again."),
        _ => (500, "INTERNAL_ERROR", "The request could not be completed.")
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = message,
        Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
    });
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
        LEFT JOIN payment_confirmations ON payments.trip_id = payment_confirmations.trip_id LIMIT 1
        """, connection);
    await command.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "healthy", database = "connected" });
});
app.Run();

public partial class Program;
