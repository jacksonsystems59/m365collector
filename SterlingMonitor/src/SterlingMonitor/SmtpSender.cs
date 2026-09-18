using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace SterlingMonitor;

public sealed class SmtpSender : IAlertSender
{
    public Task SendAsync(SmtpSettings settings, AlertMessage message, CancellationToken token) => SendTextAsync(settings,
        $"{(message.Recovery ? "RECOVERED" : message.Level.ToString().ToUpperInvariant())}: {message.ServiceName} on {Environment.MachineName}",
        $"SterlingMonitor\n\nMachine: {Environment.MachineName}\nService: {message.ServiceName}\nEvent: {(message.Recovery ? "Recovery" : "Service unhealthy")}\nSeverity: {message.Level}\nTime (UTC): {message.At:O}\n\n{message.Detail}\n\nOpen SterlingMonitor for status and activity.", token);

    public async Task SendTextAsync(SmtpSettings settings, string subject, string body, CancellationToken token)
    {
        settings.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var address in settings.RecipientList()) message.To.Add(MailboxAddress.Parse(address));
        message.Subject = $"{settings.SubjectPrefix} {subject}";
        message.Body = new TextPart("plain") { Text = body };
        using var client = new SmtpClient { Timeout = 30000, CheckCertificateRevocation = true };
        // TLS is required; use platform certificate validation, with no insecure fallback.
        await client.ConnectAsync(settings.Host, settings.Port, settings.Tls == TlsMode.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, timeout.Token);
        if (settings.Authenticate)
            await client.AuthenticateAsync(settings.Username, SecretStore.Unprotect(settings.ProtectedPassword), timeout.Token);
        await client.SendAsync(message, timeout.Token);
        // A QUIT failure after server acceptance must not cause a duplicate email retry.
        try { await client.DisconnectAsync(true, timeout.Token); } catch { }
    }
    public static string FailureHint(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => "Authentication failed. Check the username, password and SMTP provider policy.",
        SslHandshakeException => "TLS negotiation failed. Check TLS mode, port and certificate trust.",
        System.Security.Cryptography.CryptographicException => "The stored password cannot be decrypted by this Windows account. Enter it again.",
        OperationCanceledException => "The SMTP operation timed out or was cancelled.",
        SmtpCommandException => "The SMTP server rejected the sender, recipients or message. Check relay permissions.",
        _ => $"SMTP failed ({ex.GetType().Name}). Check hostname, port, firewall and provider configuration."
    };
}
