using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnifiedInbox.Application;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class SmtpMailSender(IConfiguration configuration, ILogger<SmtpMailSender> logger) : IMailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        var host = configuration["Mail:SmtpHost"] ?? Environment.GetEnvironmentVariable("SMTP_HOST") ?? "localhost";
        var port = int.TryParse(configuration["Mail:SmtpPort"] ?? Environment.GetEnvironmentVariable("SMTP_PORT"), out var parsed) ? parsed : 1025;
        var from = configuration["Mail:From"] ?? Environment.GetEnvironmentVariable("MAIL_FROM") ?? "no-reply@unifiedinbox.local";
        using var client = new SmtpClient(host, port);
        using var message = new MailMessage(from, to, subject, textBody);
        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            // Mail must never break auth flows; verification links are also returned in
            // Development/Test responses. Production logs must trigger alerting.
            logger.LogWarning(exception, "Failed to send email to {To} via {Host}:{Port}", to, host, port);
        }
    }
}
