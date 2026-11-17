using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class AlterarSenhaClienteTests
{
    private const string SenhaAtual = "SenhaAntiga@123";
    private const string SenhaNova = "SenhaNova@456";

    [Fact]
    public async Task AlterarSenhaComSenhaAtualCorretaAlteraSomenteUsuarioDaSessao()
    {
        var (controller, repository, hasher) = CriarController();
        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = SenhaAtual,
            ConfirmarSenhaAtual = SenhaAtual,
            NovaSenha = SenhaNova
        }, hasher);

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(x => x.AtualizarSenhaHash(7,
            It.Is<string>(hash => !string.IsNullOrWhiteSpace(hash))), Times.Once);
        repository.Verify(x => x.ListarUsuarioPorId(7), Times.Once);
    }

    [Fact]
    public async Task RejeitaSenhaAtualIncorretaSemAlterarHash()
    {
        var (controller, repository, hasher) = CriarController();
        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = "SenhaErrada@123",
            ConfirmarSenhaAtual = "SenhaErrada@123",
            NovaSenha = SenhaNova
        }, hasher);

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(x => x.AtualizarSenhaHash(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RejeitaConfirmacaoDaSenhaAtualDiferente()
    {
        var (controller, repository, hasher) = CriarController();
        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = SenhaAtual,
            ConfirmarSenhaAtual = "OutraSenha@123",
            NovaSenha = SenhaNova
        }, hasher);

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(x => x.ListarUsuarioPorId(It.IsAny<int>()), Times.Never);
        repository.Verify(x => x.AtualizarSenhaHash(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RejeitaNovaSenhaCurta()
    {
        var (controller, repository, hasher) = CriarController();
        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = SenhaAtual,
            ConfirmarSenhaAtual = SenhaAtual,
            NovaSenha = "123"
        }, hasher);

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(x => x.AtualizarSenhaHash(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RejeitaIdentidadeSemId()
    {
        var (controller, repository, hasher) = CriarController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = SenhaAtual,
            ConfirmarSenhaAtual = SenhaAtual,
            NovaSenha = SenhaNova
        }, hasher);

        Assert.IsType<UnauthorizedObjectResult>(result);
        repository.Verify(x => x.AtualizarSenhaHash(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SenhaLegadaValidaPodeSerSubstituida()
    {
        var repository = new Mock<IUsuarioRepository>();
        var hasher = new PasswordHasher<UsuarioModel>();
        using var pbkdf2 = new Rfc2898DeriveBytes(
            SenhaAtual, RandomNumberGenerator.GetBytes(16), 10_000, HashAlgorithmName.SHA256);
        var salt = pbkdf2.Salt;
        var legacyHash = Convert.ToBase64String(salt.Concat(pbkdf2.GetBytes(32)).ToArray());
        repository.Setup(x => x.ListarUsuarioPorId(7)).ReturnsAsync(new UsuarioModel
        {
            Id = 7, Nome = "CLIENTE", TipoUsuario = TipoUsuario.CLIENTE, Senha = legacyHash
        });
        var controller = CriarController(repository);

        var result = await controller.AlterarSenhaCliente(new AlterarSenhaClienteModel
        {
            SenhaAtual = SenhaAtual,
            ConfirmarSenhaAtual = SenhaAtual,
            NovaSenha = SenhaNova
        }, hasher);

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(x => x.AtualizarSenhaHash(7, It.IsAny<string>()), Times.Once);
    }

    private static (UsuarioController, Mock<IUsuarioRepository>, PasswordHasher<UsuarioModel>) CriarController()
    {
        var repository = new Mock<IUsuarioRepository>();
        var hasher = new PasswordHasher<UsuarioModel>();
        var usuario = new UsuarioModel
        {
            Id = 7, Nome = "CLIENTE", TipoUsuario = TipoUsuario.CLIENTE
        };
        usuario.Senha = hasher.HashPassword(usuario, SenhaAtual);
        repository.Setup(x => x.ListarUsuarioPorId(7)).ReturnsAsync(usuario);
        return (CriarController(repository), repository, hasher);
    }

    private static UsuarioController CriarController(Mock<IUsuarioRepository> repository)
    {
        var controller = new UsuarioController(
            repository.Object,
            Mock.Of<ILocalizacaoCadastroService>(),
            Mock.Of<IEmailService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "7"),
                            new Claim(ClaimTypes.Role, "CLIENTE")
                        }, "test"))
                }
            }
        };
        return controller;
    }
}
