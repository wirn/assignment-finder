using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AssignmentFinder.Notifications;

public enum DeliveryState { DryRun, Accepted, Uncertain }
public sealed record DeliveryResult(DeliveryState State, string MessageId, DateTimeOffset? AcceptedAtUtc);
public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(EmailMessage message, string recipient, string messageId, CancellationToken cancellationToken = default);
}
public sealed class LogOnlyEmailSender : IEmailSender
{
    public Task<DeliveryResult> SendAsync(EmailMessage message, string recipient, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No body, address, CV or source text is written to logs.
        return Task.FromResult(new DeliveryResult(DeliveryState.DryRun, messageId, null));
    }
}
public sealed record SmtpSettings(bool Enabled, bool ConfigurationApproved, string Host, int Port,
    string Username, string Password, string From, string Recipient)
{
    public void Validate()
    {
        if (!Enabled || !ConfigurationApproved) throw new InvalidOperationException("Riktiga utskick är inte godkända och aktiverade.");
        if (Host != "smtp.gmail.com" || Port != 587 || string.IsNullOrWhiteSpace(Password)
            || !MailboxAddress.TryParse(Username, out _) || !MailboxAddress.TryParse(From, out _)
            || !MailboxAddress.TryParse(Recipient, out _))
            throw new InvalidOperationException("Google SMTP-konfigurationen är ofullständig eller ogiltig.");
    }
}
public sealed class GoogleSmtpEmailSender(SmtpSettings settings) : IEmailSender
{
    public async Task<DeliveryResult> SendAsync(EmailMessage message, string recipient, string messageId, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (recipient != settings.Recipient) throw new InvalidOperationException("Mottagaren avviker från godkänd konfiguration.");
        using var mail = new MimeMessage { Subject = message.Subject, MessageId = messageId };
        mail.From.Add(MailboxAddress.Parse(settings.From));
        mail.To.Add(MailboxAddress.Parse(settings.Recipient));
        mail.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        using var client = new SmtpClient { Timeout = 30000 };
        // STARTTLS is required, with MailKit's default certificate validation.
        await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
        try
        {
            await client.SendAsync(mail, cancellationToken);
        }
        catch (Exception error) when (error is IOException or SmtpProtocolException or SmtpCommandException or OperationCanceledException)
        {
            // Once transmission starts, never retry an ambiguous outcome automatically.
            return new(DeliveryState.Uncertain, messageId, null);
        }
        try { await client.DisconnectAsync(true, CancellationToken.None); }
        catch (Exception error) when (error is IOException or SmtpProtocolException or SmtpCommandException or OperationCanceledException) { }
        return new(DeliveryState.Accepted, messageId, DateTimeOffset.UtcNow);
    }
}
