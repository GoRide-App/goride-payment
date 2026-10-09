using System.Globalization;
using System.Net;
using System.Text;
using GoRide.Payment.Models;

namespace GoRide.Payment.Receipts;

// Everything a receipt shows. Amount is the verified paid amount (equal to the final fare).
public sealed record ReceiptContent(
    string ReceiptId, string TripId, string Recipient, string? RecipientName,
    long AmountMinor, string Currency, string? CardBrand, string? CardLast4,
    string ProviderReference, DateTimeOffset PaidAt, FareBreakdown? Breakdown, string Provider = "PayHere");

public static class ReceiptRenderer
{
    private static readonly CultureInfo Money = CultureInfo.InvariantCulture;

    public static EmailMessage Render(ReceiptContent receipt)
    {
        var total = Format(receipt.AmountMinor / 100m, receipt.Currency);
        var paidAt = ToSriLankaTime(receipt.PaidAt).ToString("d MMM yyyy, h:mm tt", CultureInfo.GetCultureInfo("en-GB")) + " (Sri Lanka time)";
        var card = string.Join(" ", new[] { receipt.CardBrand, receipt.CardLast4 is null ? null : "ending " + receipt.CardLast4 }
            .Where(part => !string.IsNullOrEmpty(part)));
        if (card.Length == 0) card = "Card";
        var greeting = string.IsNullOrWhiteSpace(receipt.RecipientName) ? "Hi there" : "Hi " + receipt.RecipientName.Split(' ')[0];

        var rows = new List<(string Label, string Value)>();
        if (receipt.Breakdown is { } b)
        {
            rows.Add(("Base fare", Format(b.Base, receipt.Currency)));
            rows.Add(("Distance", Format(b.Distance, receipt.Currency)));
            rows.Add(("Time", Format(b.Time, receipt.Currency)));
            if (b.Stops > 0) rows.Add(("Stops", Format(b.Stops, receipt.Currency)));
            if (b.Waiting > 0) rows.Add(("Waiting", Format(b.Waiting, receipt.Currency)));
        }

        var subject = $"Your GoRide receipt · {total}";
        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"margin:0;background:#f5f5f3;font-family:Arial,Helvetica,sans-serif;color:#111111\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"padding:24px 12px\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#ffffff;border-radius:16px;overflow:hidden\">");
        html.Append("<tr><td style=\"background:#ffc21a;padding:20px 24px;font-size:22px;font-weight:bold\">GoRide</td></tr>");
        html.Append("<tr><td style=\"padding:24px\">");
        html.Append($"<p style=\"margin:0 0 4px;font-size:15px\">{E(greeting)},</p>");
        html.Append("<p style=\"margin:0 0 20px;font-size:15px;color:#555555\">Thanks for riding with GoRide. Your card payment was successful.</p>");
        html.Append("<p style=\"margin:0;font-size:13px;color:#6e6e73\">Total paid</p>");
        html.Append($"<p style=\"margin:4px 0 20px;font-size:32px;font-weight:bold\">{E(total)}</p>");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:14px;border-top:1px solid #ececea\">");
        foreach (var (label, value) in rows) AppendRow(html, label, value, false);
        AppendRow(html, "Total", total, true);
        AppendRow(html, "Paid with", card, false);
        AppendRow(html, "Paid on", paidAt, false);
        AppendRow(html, ReferenceLabel(receipt), receipt.ProviderReference, false);
        AppendRow(html, "Trip reference", receipt.TripId, false);
        AppendRow(html, "Receipt number", receipt.ReceiptId, false);
        html.Append("</table>");
        html.Append($"<p style=\"margin:20px 0 0;font-size:12px;color:#6e6e73\">{E(Footer(receipt))}</p>");
        html.Append("</td></tr></table></td></tr></table></body></html>");

        var text = new StringBuilder();
        text.AppendLine($"{greeting},").AppendLine().AppendLine("Thanks for riding with GoRide. Your card payment was successful.").AppendLine();
        foreach (var (label, value) in rows) text.AppendLine($"{label}: {value}");
        text.AppendLine($"Total paid: {total}")
            .AppendLine($"Paid with: {card}")
            .AppendLine($"Paid on: {paidAt}")
            .AppendLine($"{ReferenceLabel(receipt)}: {receipt.ProviderReference}")
            .AppendLine($"Trip reference: {receipt.TripId}")
            .AppendLine($"Receipt number: {receipt.ReceiptId}");

        return new(receipt.Recipient, receipt.RecipientName, subject, html.ToString(), text.ToString(), receipt.ReceiptId);
    }

    // In-app demo card payments are not PayHere payments, so they say so.
    private static string ReferenceLabel(ReceiptContent receipt) =>
        receipt.Provider == "PayHere" ? "PayHere reference" : "Payment reference";

    private static string Footer(ReceiptContent receipt) => receipt.Provider == "PayHere"
        ? "This receipt was sent because a card payment for your GoRide trip was confirmed by PayHere. Keep it for your records."
        : "This receipt was sent because a card payment for your GoRide trip was successful. Keep it for your records.";

    public static string Format(decimal amount, string currency) =>
        currency + " " + amount.ToString("#,##0.00", Money);

    public static DateTimeOffset ToSriLankaTime(DateTimeOffset value)
    {
        try { return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo")); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return value.ToOffset(TimeSpan.FromMinutes(330));
        }
    }

    private static void AppendRow(StringBuilder html, string label, string value, bool strong)
    {
        var weight = strong ? "font-weight:bold;" : "";
        html.Append($"<tr><td style=\"padding:10px 0;border-bottom:1px solid #ececea;color:#6e6e73\">{E(label)}</td>");
        html.Append($"<td align=\"right\" style=\"padding:10px 0;border-bottom:1px solid #ececea;{weight}\">{E(value)}</td></tr>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
