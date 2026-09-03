using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class ForgotPasswordControllerEmailTests
{
    [Fact]
    public async Task EnviarCodigoConfirmaSomenteDepoisDoEnvioSmtp()
    {
        var emailService = new Mock<IEmailService>();
        var forgotPasswordRepository = new Mock<IForgotPasswordRepository>();
        var recuperacaoRepository = new Mock<IRecuperacaoSenhaRepository>();
        forgotPasswordRepository
            .Setup(item => item.ObterUsuarioIdPorEmail("usuario@example.com"))
            .ReturnsAsync(CriarUsuario());
        forgotPasswordRepository
            .Setup(item => item.SalvarCodigoRecuperacao(42, It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        emailService
            .Setup(item => item.EnviarCodigoAsync(
                "usuario@example.com",
                It.Is<string>(codigo => codigo.Length == 6),
                "Usuario Teste"))
            .Returns(Task.CompletedTask);
        var controller = CriarController(
            emailService,
            forgotPasswordRepository,
            recuperacaoRepository);

        var resultado = await controller.EnviarCodigo(new EnviarCodigoModel
        {
            Email = "usuario@example.com"
        });

        Assert.IsType<OkObjectResult>(resultado);
        forgotPasswordRepository.Verify(
            item => item.InvalidarCodigosRecuperacao(It.IsAny<int>()),
            Times.Never);
        emailService.VerifyAll();
        forgotPasswordRepository.VerifyAll();
    }

    [Fact]
    public async Task EnviarCodigoRetorna503EInvalidaCodigoQuandoSmtpFalha()
    {
        var emailService = new Mock<IEmailService>();
        var forgotPasswordRepository = new Mock<IForgotPasswordRepository>();
        var recuperacaoRepository = new Mock<IRecuperacaoSenhaRepository>();
        forgotPasswordRepository
            .Setup(item => item.ObterUsuarioIdPorEmail("usuario@example.com"))
            .ReturnsAsync(CriarUsuario());
        forgotPasswordRepository
            .Setup(item => item.SalvarCodigoRecuperacao(42, It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        forgotPasswordRepository
            .Setup(item => item.InvalidarCodigosRecuperacao(42))
            .Returns(Task.CompletedTask);
        emailService
            .Setup(item => item.EnviarCodigoAsync(
                "usuario@example.com",
                It.IsAny<string>(),
                "Usuario Teste"))
            .ThrowsAsync(new EmailEnvioException(
                "smtp_authentication",
                "Falha SMTP de teste.",
                new InvalidOperationException("SMTP indisponivel.")));
        var controller = CriarController(
            emailService,
            forgotPasswordRepository,
            recuperacaoRepository);

        var resultado = await controller.EnviarCodigo(new EnviarCodigoModel
        {
            Email = "usuario@example.com"
        });

        var resposta = Assert.IsType<ObjectResult>(resultado);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, resposta.StatusCode);
        Assert.Equal(
            "smtp_authentication",
            resposta.Value?.GetType().GetProperty("codigo")?.GetValue(resposta.Value));
        emailService.VerifyAll();
        forgotPasswordRepository.VerifyAll();
    }

    private static UsuarioEmail CriarUsuario() => new()
    {
        Id = 42,
        Email = "usuario@example.com",
        Nome = "Usuario Teste"
    };

    private static ForgotPasswordController CriarController(
        Mock<IEmailService> emailService,
        Mock<IForgotPasswordRepository> forgotPasswordRepository,
        Mock<IRecuperacaoSenhaRepository> recuperacaoRepository)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PasswordRecovery:Pepper"] = "pepper-de-teste-com-pelo-menos-32-bytes"
            })
            .Build();

        return new ForgotPasswordController(
            emailService.Object,
            forgotPasswordRepository.Object,
            recuperacaoRepository.Object,
            configuration,
            Mock.Of<ILogger<ForgotPasswordController>>());
    }
}
