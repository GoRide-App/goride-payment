namespace GoRide.Payment.Services;

public sealed class PaymentException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
