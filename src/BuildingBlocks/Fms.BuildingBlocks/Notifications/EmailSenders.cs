using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Notifications;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"None" (default: emails are not sent, only logged without their content) or "Smtp".</summary>
    public string Provider { get; set; } = "None";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }

    /// <summary>Keep this in a secret store (environment variable, Key Vault), never in a file in the repository.</summary>
    public string? Password { get; set; }

    public string? From { get; set; }
    public string FromName { get; set; } = "Facility Management";
}

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions o)
    {
        if (o.Provider.Equals("None", StringComparison.OrdinalIgnoreCase)) return ValidateOptionsResult.Success;
        if (!o.Provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase)) return ValidateOptionsResult.Fail("Email:Provider must be None or Smtp.");
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.From)) return ValidateOptionsResult.Fail("Email:Host and Email:From are required for Smtp.");
        if (o.Port is < 1 or > 65535) return ValidateOptionsResult.Fail("Email:Port is not valid.");
        return ValidateOptionsResult.Success;
    }
}

/// <summary>Default: no email is sent. It logs that one was suppressed (never the address or content).</summary>
public sealed class NullEmailSender(ILogger<NullEmailSender> log) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        log.LogInformation("Email not sent: no email provider is configured (Email:Provider=None)");
        return Task.CompletedTask;
    }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        using var mail = new MailMessage
        {
            From = new MailAddress(o.From!, o.FromName, Encoding.UTF8),
            Subject = message.Subject.ReplaceLineEndings(" "),
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        mail.To.Add(new MailAddress(message.To));

#pragma warning disable SYSLIB0014 // SmtpClient is limited but sufficient for transactional notices; the interface lets a provider SDK replace it
        using var client = new SmtpClient(o.Host!, o.Port) { EnableSsl = o.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000 };
#pragma warning restore SYSLIB0014
        if (!string.IsNullOrWhiteSpace(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);
        await client.SendMailAsync(mail, ct);
    }
}

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddFmsEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.SectionName).ValidateOnStart();
        services.Replace(ServiceDescriptor.Singleton<IEmailSender>(sp =>
            sp.GetRequiredService<IOptions<EmailOptions>>().Value.Provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
                ? new SmtpEmailSender(sp.GetRequiredService<IOptions<EmailOptions>>())
                : new NullEmailSender(sp.GetRequiredService<ILogger<NullEmailSender>>())));
        return services;
    }
}
