using MailKit.Net.Smtp;

namespace SaaS.Infrastructure.Services;

public interface ISmtpClientFactory
{
    ISmtpClient Create();
}

public sealed class SmtpClientFactory : ISmtpClientFactory
{
    public ISmtpClient Create() => new SmtpClient();
}
