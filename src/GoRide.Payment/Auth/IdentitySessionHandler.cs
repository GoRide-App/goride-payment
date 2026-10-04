using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using GoRide.Payment.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GoRide.Payment.Auth;

// The existing identity service owns the Asgardeo session cookie. Validate it there
// instead of trusting a riderId supplied by the browser or sharing encryption keys.
public sealed class IdentitySessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IHttpClientFactory clients)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IdentitySession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Cookie", out var cookie)) return AuthenticateResult.NoResult();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/me");
        request.Headers.TryAddWithoutValidation("Cookie", cookie.ToString());
        try
        {
            using var response = await clients.CreateClient("Identity").SendAsync(request, Context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthenticateResult.Fail("The session is invalid or expired.");
            if (!response.IsSuccessStatusCode)
                throw new PaymentException(503, "IDENTITY_UNAVAILABLE", "The session could not be verified. Please try again.");
            var session = await response.Content.ReadFromJsonAsync<IdentitySession>(Context.RequestAborted);
            if (string.IsNullOrWhiteSpace(session?.UserId)) return AuthenticateResult.Fail("The session has no user ID.");
            if (response.Headers.TryGetValues("Set-Cookie", out var renewedCookies))
                foreach (var renewed in renewedCookies) Response.Headers.Append("Set-Cookie", renewed);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", session.UserId)], SchemeName));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !Context.RequestAborted.IsCancellationRequested)
        {
            throw new PaymentException(503, "IDENTITY_UNAVAILABLE", "The session could not be verified. Please try again.");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteError(401, "AUTHENTICATION_REQUIRED", "Sign in to select a payment method.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteError(403, "PAYMENT_FORBIDDEN", "You do not have access to this payment.");

    private Task WriteError(int status, string code, string title)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(new { status, code, title }, Context.RequestAborted);
    }

    private sealed record IdentitySession(string? UserId);
}
