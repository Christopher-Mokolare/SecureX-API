using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using SecureX.Api.Models;

namespace SecureX.Api.Services;

/// <summary>
/// Wrapper around Amazon SES v2 for sending transactional emails.
///
/// All sends are fire-and-forget friendly: if SES is down, rate-limited,
/// or rejects a recipient (e.g. sandbox mode), we log and return without
/// throwing. Transaction state is already committed by the time these run.
///
/// Sender configuration comes from env vars (mapped in Program.cs):
///   SES_FROM_ADDRESS, SES_FROM_NAME, SES_REPLY_TO, SES_ADMIN_EMAIL, SES_FRONTEND_BASE
/// </summary>
public class EmailService(
    IAmazonSimpleEmailServiceV2 ses,
    IConfiguration config,
    ILogger<EmailService> logger)
{
    private readonly string _fromAddress = config["Ses:FromAddress"] ?? "noreply@secureexchange.co.za";
    private readonly string _fromName = config["Ses:FromName"] ?? "SecureX";
    private readonly string _replyTo = config["Ses:ReplyTo"] ?? "info@secureexchange.co.za";
    private readonly string _adminEmail = config["Ses:AdminEmail"] ?? "info@secureexchange.co.za";
    private readonly string _frontendBase = config["Ses:FrontendBase"] ?? "https://www.secureexchange.co.za";
    private const string LogoUrl = "https://www.secureexchange.co.za/favicon.png";

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Sent to the seller the moment the buyer's payment lands in escrow.
    /// Contains the seller's deal-token link to begin bank-details + liveness verification.
    /// </summary>
    public async Task SendSellerVerificationLinkAsync(User seller, Transaction tx, string dealToken)
    {
        var url = $"{_frontendBase}/bank-details/{seller.Id}?t={Uri.EscapeDataString(dealToken)}";
        var payout = tx.ItemValue - tx.SellerFee;
        var firstName = FirstName(seller.FullName);

        var subject = $"Action required — verify to receive R{payout:N2} for {tx.ItemTitle}";
        var html = SellerVerificationHtml(firstName, tx, payout, url);
        var text = SellerVerificationText(firstName, tx, payout, url);

        await SendAsync(seller.Email, subject, html, text);
    }

    /// <summary>
    /// Sent to the buyer as confirmation that payment cleared and the seller
    /// has been notified.
    /// </summary>
    public async Task SendEscrowFundedAsync(User buyer, Transaction tx)
    {
        var firstName = FirstName(buyer.FullName);
        var subject = $"Escrow funded — {tx.DealReference}";
        var html = EscrowFundedHtml(firstName, tx);
        var text = EscrowFundedText(firstName, tx);

        await SendAsync(buyer.Email, subject, html, text);
    }

    /// <summary>
    /// Sent to the ops inbox when a buyer rejects an item within the
    /// inspection window and a dispute is opened.
    /// </summary>
    public async Task SendAdminDisputeAlertAsync(Transaction tx, string reason)
    {
        var subject = $"[SecureX] Dispute raised — {tx.DealReference}";
        var html = AdminDisputeHtml(tx, reason);
        var text = AdminDisputeText(tx, reason);

        await SendAsync(_adminEmail, subject, html, text);
    }

    // ── Internal send ───────────────────────────────────────────────────

    private async Task SendAsync(string to, string subject, string htmlBody, string textBody)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogWarning("EmailService: no recipient for subject '{Subject}'", subject);
            return;
        }

        var request = new SendEmailRequest
        {
            FromEmailAddress = $"{_fromName} <{_fromAddress}>",
            Destination = new Destination { ToAddresses = [to] },
            ReplyToAddresses = [ _replyTo ],
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Html = new Content { Data = htmlBody, Charset = "UTF-8" },
                        Text = new Content { Data = textBody, Charset = "UTF-8" }
                    }
                }
            }
        };

        try
        {
            var response = await ses.SendEmailAsync(request);
            logger.LogInformation("Email sent to {To} subject='{Subject}' messageId={MessageId}",
                to, subject, response.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email send failed to {To} subject='{Subject}'", to, subject);
            // Swallow — email failures must not break the transaction flow.
        }
    }

    private static string FirstName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "there";
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : "there";
    }

    private static string Money(decimal amount) => $"R{amount:N2}";

    private static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    // ── HTML templates ──────────────────────────────────────────────────

    private string WrapHtml(string title, string bodyHtml) => $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>{Escape(title)}</title>
</head>
<body style=""margin:0;padding:0;background:#f1f5f9;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#0f172a;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f1f5f9;padding:32px 16px;"">
    <tr><td align=""center"">
      <table width=""100%"" style=""max-width:560px;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,0.06);"">
        <tr>
          <td style=""padding:24px 32px;border-bottom:1px solid #e2e8f0;background:linear-gradient(135deg,#1e3a8a 0%,#1d4ed8 100%);"">
            <table width=""100%"" cellpadding=""0"" cellspacing=""0""><tr>
              <td style=""vertical-align:middle;"">
                <img src=""{LogoUrl}"" alt=""SecureX"" width=""40"" height=""40"" style=""display:block;border-radius:8px;"">
              </td>
              <td style=""vertical-align:middle;padding-left:12px;color:#ffffff;font-size:20px;font-weight:700;letter-spacing:0.5px;"">
                SecureX
              </td>
            </tr></table>
          </td>
        </tr>
        <tr><td style=""padding:32px;"">
          {bodyHtml}
        </td></tr>
        <tr><td style=""padding:20px 32px;border-top:1px solid #e2e8f0;background:#f8fafc;color:#64748b;font-size:12px;line-height:1.5;"">
          SecureX &middot; <a href=""{_frontendBase}"" style=""color:#1d4ed8;text-decoration:none;"">secureexchange.co.za</a><br>
          You are receiving this email because you are a party to a SecureX escrow transaction.
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";

    private string SellerVerificationHtml(string firstName, Transaction tx, decimal payout, string url) => WrapHtml(
        $"Action required — {tx.DealReference}",
        $@"<h1 style=""margin:0 0 16px;font-size:22px;color:#0f172a;"">Hello {Escape(firstName)},</h1>

<p style=""margin:0 0 16px;font-size:15px;line-height:1.6;color:#334155;"">
  A buyer has secured funds in escrow for your item. To receive your payout, please verify your identity.
</p>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f8fafc;border-radius:8px;padding:20px;margin:20px 0;"">
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Deal reference</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.DealReference)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Item</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.ItemTitle)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Item value</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Money(tx.ItemValue)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Your payout</td><td style=""padding:6px 0;text-align:right;color:#16a34a;font-size:15px;font-weight:700;"">{Money(payout)}</td></tr>
</table>

<p style=""margin:24px 0 12px;font-size:15px;line-height:1.6;color:#334155;"">
  To receive your payout, complete the following steps:
</p>
<ol style=""margin:0 0 24px;padding-left:20px;font-size:15px;line-height:1.8;color:#334155;"">
  <li>Submit your bank account details</li>
  <li>Complete a biometric liveness check (&lt; 2 minutes on your phone)</li>
  <li>Ship the item to the buyer</li>
</ol>

<table cellpadding=""0"" cellspacing=""0"" style=""margin:28px 0;""><tr><td>
  <a href=""{url}"" style=""display:inline-block;padding:14px 28px;background:#1d4ed8;color:#ffffff;text-decoration:none;border-radius:8px;font-weight:600;font-size:15px;"">
    Verify my identity and receive payout
  </a>
</td></tr></table>

<p style=""margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;"">
  This link is unique to this transaction. Do not share it.
</p>

<p style=""margin:24px 0 0;font-size:13px;line-height:1.6;color:#64748b;"">
  Questions? Reply to this email and we'll respond from info@secureexchange.co.za.
</p>"
    );

    private string SellerVerificationText(string firstName, Transaction tx, decimal payout, string url) =>
        $@"Hello {firstName},

A buyer has secured funds in escrow for your item. To receive your payout, please verify your identity.

Deal reference: {tx.DealReference}
Item: {tx.ItemTitle}
Item value: {Money(tx.ItemValue)}
Your payout: {Money(payout)}

Next steps:
1. Submit your bank account details
2. Complete a biometric liveness check (< 2 minutes on your phone)
3. Ship the item to the buyer

Verify here:
{url}

This link is unique to this transaction. Do not share it.

Questions? Reply to this email.

— SecureX
{_frontendBase}";

    private string EscrowFundedHtml(string firstName, Transaction tx) => WrapHtml(
        $"Escrow funded — {tx.DealReference}",
        $@"<h1 style=""margin:0 0 16px;font-size:22px;color:#0f172a;"">Hello {Escape(firstName)},</h1>

<p style=""margin:0 0 16px;font-size:15px;line-height:1.6;color:#334155;"">
  Your payment has been received and is now held securely in escrow. The seller has been notified and will complete their verification.
</p>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f8fafc;border-radius:8px;padding:20px;margin:20px 0;"">
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Deal reference</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.DealReference)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Item</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.ItemTitle)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Amount in escrow</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Money(tx.TotalCheckoutAmount)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Seller</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.Seller?.Email ?? "")}</td></tr>
</table>

<p style=""margin:24px 0 12px;font-size:15px;line-height:1.6;color:#334155;"">
  <strong>What happens next:</strong>
</p>
<ul style=""margin:0 0 24px;padding-left:20px;font-size:15px;line-height:1.8;color:#334155;"">
  <li>The seller verifies their identity and arranges shipping</li>
  <li>You'll be notified when the item is on its way</li>
  <li>Once delivered, you have 24 hours to inspect and accept</li>
</ul>

<p style=""margin:24px 0 0;font-size:13px;line-height:1.6;color:#64748b;"">
  Track your deal anytime from your buyer portal. Reply to this email if you have questions.
</p>"
    );

    private string EscrowFundedText(string firstName, Transaction tx) =>
        $@"Hello {firstName},

Your payment has been received and is now held securely in escrow.

Deal reference: {tx.DealReference}
Item: {tx.ItemTitle}
Amount in escrow: {Money(tx.TotalCheckoutAmount)}
Seller: {tx.Seller?.Email ?? ""}

The seller has been notified and will complete their verification.

What happens next:
- The seller verifies their identity and arranges shipping
- You'll be notified when the item is on its way
- Once delivered, you have 24 hours to inspect and accept

— SecureX
{_frontendBase}";

    private string AdminDisputeHtml(Transaction tx, string reason) => WrapHtml(
        $"Dispute raised — {tx.DealReference}",
        $@"<h1 style=""margin:0 0 16px;font-size:22px;color:#b91c1c;"">Dispute raised on {Escape(tx.DealReference)}</h1>

<p style=""margin:0 0 16px;font-size:15px;line-height:1.6;color:#334155;"">
  A buyer has rejected an item and a dispute has been opened. Manual review required within 72 hours.
</p>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#fef2f2;border-radius:8px;padding:20px;margin:20px 0;border-left:4px solid #dc2626;"">
  <tr><td style=""padding:6px 0;color:#991b1b;font-size:13px;font-weight:600;"">Reason</td></tr>
  <tr><td style=""padding:6px 0 16px;color:#450a0a;font-size:14px;line-height:1.5;"">{Escape(reason)}</td></tr>
</table>

<table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f8fafc;border-radius:8px;padding:20px;margin:20px 0;"">
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Deal reference</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.DealReference)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Item</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.ItemTitle)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Value</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Money(tx.ItemValue)}</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Buyer</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.Buyer?.FullName ?? "")} &lt;{Escape(tx.Buyer?.Email ?? "")}&gt;</td></tr>
  <tr><td style=""padding:6px 0;color:#64748b;font-size:13px;"">Seller</td><td style=""padding:6px 0;text-align:right;color:#0f172a;font-size:13px;font-weight:600;"">{Escape(tx.Seller?.FullName ?? "")} &lt;{Escape(tx.Seller?.Email ?? "")}&gt;</td></tr>
</table>

<table cellpadding=""0"" cellspacing=""0"" style=""margin:28px 0;""><tr><td>
  <a href=""{_frontendBase}/admin"" style=""display:inline-block;padding:14px 28px;background:#1d4ed8;color:#ffffff;text-decoration:none;border-radius:8px;font-weight:600;font-size:15px;"">
    Open admin panel
  </a>
</td></tr></table>

<p style=""margin:0;font-size:13px;line-height:1.6;color:#64748b;"">
  Review within 72 hours per our SLA. Both parties will be notified of the outcome.
</p>"
    );

    private string AdminDisputeText(Transaction tx, string reason) =>
        $@"DISPUTE RAISED — {tx.DealReference}

A buyer has rejected an item and a dispute has been opened. Manual review required within 72 hours.

Reason: {reason}

Deal reference: {tx.DealReference}
Item: {tx.ItemTitle}
Value: {Money(tx.ItemValue)}
Buyer: {tx.Buyer?.FullName ?? ""} <{tx.Buyer?.Email ?? ""}>
Seller: {tx.Seller?.FullName ?? ""} <{tx.Seller?.Email ?? ""}>

Open admin panel: {_frontendBase}/admin

— SecureX";
}
