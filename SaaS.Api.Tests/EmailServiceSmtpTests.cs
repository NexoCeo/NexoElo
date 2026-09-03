using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Moq;
using SaaS.Application.Exceptions;
using SaaS.Infrastructure.Services;
using Xunit;

namespace SaaS.Api.Tests;

public class EmailServiceSmtpTests
{
    [Fact]
    public async Task FalhaDeAutenticacaoRetornaCodigoSeguro()
    {
        var smtpClient = new Mock<ISmtpClient>();
        smtpClient
            .Setup(item => item.ConnectAsync(
                "smtp.zoho.test",
                587,
                SecureSocketOptions.StartTls,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.AuthenticateAsync(
                "smtp-user@nexoceo.test",
                "senha-smtp-de-teste",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MailKit.Security.AuthenticationException("Credencial recusada."));
        var factory = new Mock<ISmtpClientFactory>();
        factory.Setup(item => item.Create()).Returns(smtpClient.Object);
        var configuracao = CriarConfiguracao();
        var service = new EmailService(configuracao, factory.Object);

        var exception = await Assert.ThrowsAsync<EmailEnvioException>(() =>
            service.EnviarCodigoAsync("usuario@example.com", "381492", "Usuario Teste"));

        Assert.Equal("smtp_authentication", exception.Codigo);
        smtpClient.VerifyAll();
    }

    [Fact]
    public async Task CredenciaisProfissionalUsamConfiguracaoSmtpEContemAcesso()
    {
        var smtpClient = new Mock<ISmtpClient>();
        MimeMessage? mensagemEnviada = null;
        smtpClient
            .Setup(item => item.ConnectAsync(
                "smtp.zoho.test",
                587,
                SecureSocketOptions.StartTls,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.AuthenticateAsync(
                "smtp-user@nexoceo.test",
                "senha-smtp-de-teste",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.SendAsync(
                It.IsAny<MimeMessage>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ITransferProgress?>()))
            .Callback<MimeMessage, CancellationToken, ITransferProgress?>(
                (mensagem, _, _) => mensagemEnviada = mensagem)
            .ReturnsAsync("message-id");
        smtpClient
            .Setup(item => item.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var factory = new Mock<ISmtpClientFactory>();
        factory.Setup(item => item.Create()).Returns(smtpClient.Object);
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "acesso@nexoceo.test",
                ["Email:Name"] = "NexoCEO",
                ["Email:Username"] = "smtp-user@nexoceo.test",
                ["Email:Password"] = "senha-smtp-de-teste",
                ["Email:SmtpHost"] = "smtp.zoho.test",
                ["Email:SmtpPort"] = "587"
            })
            .Build();
        var service = new EmailService(configuracao, factory.Object);

        await service.EnviarCredenciaisProfissionalAsync(
            "profissional@example.com",
            "profissional@example.com",
            "SenhaTemporaria9!",
            "Profissional Teste");

        Assert.NotNull(mensagemEnviada);
        Assert.Equal("Seu acesso profissional ao NexoCEO", mensagemEnviada.Subject);
        Assert.Equal("acesso@nexoceo.test", ((MailboxAddress)mensagemEnviada.From[0]).Address);
        Assert.Equal("profissional@example.com", ((MailboxAddress)mensagemEnviada.To[0]).Address);
        var corpo = Assert.IsType<TextPart>(mensagemEnviada.Body).Text;
        Assert.Contains("profissional@example.com", corpo);
        Assert.Contains("SenhaTemporaria9!", corpo);
        smtpClient.VerifyAll();
    }

    [Fact]
    public async Task CredencialSempreUsaUsernameEPasswordComStartTls()
    {
        var smtpClient = new Mock<ISmtpClient>();
        smtpClient
            .Setup(item => item.ConnectAsync(
                "smtp.zoho.test",
                587,
                SecureSocketOptions.StartTls,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.AuthenticateAsync(
                "smtp-oauth@nexoceo.test",
                "1000.credencial-smtp-de-teste",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.SendAsync(
                It.IsAny<MimeMessage>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ITransferProgress?>()))
            .ReturnsAsync("message-id");
        smtpClient
            .Setup(item => item.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var factory = new Mock<ISmtpClientFactory>();
        factory.Setup(item => item.Create()).Returns(smtpClient.Object);
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "acesso@nexoceo.test",
                ["Email:Name"] = "NexoCEO",
                ["Email:Username"] = "smtp-oauth@nexoceo.test",
                ["Email:Password"] = "1000.credencial-smtp-de-teste",
                ["Email:SmtpHost"] = "smtp.zoho.test",
                ["Email:SmtpPort"] = "587"
            })
            .Build();
        var service = new EmailService(configuracao, factory.Object);

        await service.EnviarCredenciaisProfissionalAsync(
            "profissional@example.com",
            "profissional@example.com",
            "SenhaTemporaria9!",
            "Profissional Teste");

        smtpClient.VerifyAll();
    }

    [Fact]
    public async Task CodigoRecuperacaoUsaAsMesmasConfiguracoesSmtp()
    {
        var smtpClient = new Mock<ISmtpClient>();
        MimeMessage? mensagemEnviada = null;
        smtpClient
            .Setup(item => item.ConnectAsync(
                "smtp.zoho.test",
                587,
                SecureSocketOptions.StartTls,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.AuthenticateAsync(
                "smtp-user@nexoceo.test",
                "senha-smtp-de-teste",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        smtpClient
            .Setup(item => item.SendAsync(
                It.IsAny<MimeMessage>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ITransferProgress?>()))
            .Callback<MimeMessage, CancellationToken, ITransferProgress?>(
                (mensagem, _, _) => mensagemEnviada = mensagem)
            .ReturnsAsync("message-id");
        smtpClient
            .Setup(item => item.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var factory = new Mock<ISmtpClientFactory>();
        factory.Setup(item => item.Create()).Returns(smtpClient.Object);
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "acesso@nexoceo.test",
                ["Email:Name"] = "NexoCEO",
                ["Email:Username"] = "smtp-user@nexoceo.test",
                ["Email:Password"] = "senha-smtp-de-teste",
                ["Email:SmtpHost"] = "smtp.zoho.test",
                ["Email:SmtpPort"] = "587"
            })
            .Build();
        var service = new EmailService(configuracao, factory.Object);

        await service.EnviarCodigoAsync(
            "usuario@example.com",
            "381492",
            "Usuario Teste");

        Assert.NotNull(mensagemEnviada);
        Assert.Equal("Código de recuperação de senha", mensagemEnviada.Subject);
        Assert.Equal("acesso@nexoceo.test", ((MailboxAddress)mensagemEnviada.From[0]).Address);
        Assert.Equal("usuario@example.com", ((MailboxAddress)mensagemEnviada.To[0]).Address);
        var corpo = Assert.IsType<TextPart>(mensagemEnviada.Body).Text;
        Assert.Contains("381492", corpo);
        Assert.Contains("Usuario Teste", corpo);
        smtpClient.VerifyAll();
    }

    private static IConfiguration CriarConfiguracao()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "acesso@nexoceo.test",
                ["Email:Name"] = "NexoCEO",
                ["Email:Username"] = "smtp-user@nexoceo.test",
                ["Email:Password"] = "senha-smtp-de-teste",
                ["Email:SmtpHost"] = "smtp.zoho.test",
                ["Email:SmtpPort"] = "587"
            })
            .Build();
    }
}
