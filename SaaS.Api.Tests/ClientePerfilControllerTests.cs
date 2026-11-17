using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class ClientePerfilControllerTests
{
    [Fact]
    public async Task ClienteAtualizaProprioPerfilPelaRotaExistente()
    {
        var repository = new Mock<IUsuarioRepository>();
        repository.Setup(x => x.ListarUsuarioPorId(7)).ReturnsAsync(new UsuarioModel
        {
            Id = 7, Nome = "CLIENTE", TipoUsuario = TipoUsuario.CLIENTE
        });
        repository.Setup(x => x.AtualizarPerfilUsuario(7, "Novo nome",
            "novo@example.com", "11999998888", null, false)).ReturnsAsync(new UsuarioModel
            {
                Id = 7, Nome = "NOVO NOME", Email = "novo@example.com",
                Telefone = "11999998888", TipoUsuario = TipoUsuario.CLIENTE
            });
        var controller = CreateController(repository);

        var result = await controller.AtualizarPerfil(7, new UpdatePerfilModel
        {
            Nome = "Novo nome", Email = "novo@example.com", Telefone = "11999998888"
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(x => x.AtualizarPerfilUsuario(7, "Novo nome",
            "novo@example.com", "11999998888", null, false), Times.Once);
    }

    [Fact]
    public async Task ClienteNaoAlteraPerfilDeOutroUsuario()
    {
        var repository = new Mock<IUsuarioRepository>();
        var controller = CreateController(repository);
        var result = await controller.AtualizarPerfil(15, new UpdatePerfilModel
        {
            Nome = "Nome alterado", Email = "novo@example.com"
        });

        Assert.IsType<ForbidResult>(result);
        repository.Verify(x => x.AtualizarPerfilUsuario(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ClienteSemEmailNemTelefoneRecebeErro()
    {
        var repository = new Mock<IUsuarioRepository>();
        var controller = CreateController(repository);
        var result = await controller.AtualizarPerfil(7, new UpdatePerfilModel
        {
            Nome = "Nome alterado"
        });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static UsuarioController CreateController(Mock<IUsuarioRepository> repository)
    {
        return new UsuarioController(
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
    }
}
