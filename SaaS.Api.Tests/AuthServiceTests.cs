using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using SaaS.Infrastructure.Services;
using Xunit;

namespace SaaS.Api.Tests;

public class AuthServiceTests
{
    private static readonly IConfiguration Configuration =
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-key-with-at-least-thirty-two-bytes-long",
                ["Jwt:Issuer"] = "SaaS.Api.Tests",
                ["Jwt:Audience"] = "SaaS.Web.Tests",
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test"
            })
            .Build();

    [Fact]
    public async Task TokenIncluiPapelDoUsuario()
    {
        var service = CriarServico(Mock.Of<IUsuarioRepository>());

        var token = await service.GenerateJwtToken(new UsuarioModel
        {
            Id = 7,
            Nome = "Empresa",
            TipoUsuario = TipoUsuario.EMPRESA
        });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Contains(jwt.Claims, claim =>
            claim.Type is ClaimTypes.Role or "role" && claim.Value == "EMPRESA");
    }

    [Fact]
    public async Task LoginLegadoAtualizaHashGradualmente()
    {
        const string senha = "senha-segura";
        var hashLegado = CriarHashLegado(senha);
        var usuario = new UsuarioModel
        {
            Id = 4,
            Nome = "Cliente",
            Email = "cliente@example.com",
            Senha = hashLegado,
            TipoUsuario = TipoUsuario.CLIENTE
        };
        var repository = new Mock<IUsuarioRepository>();
        repository.Setup(item => item.ListarUsuarioPorLogin(usuario.Email)).ReturnsAsync(usuario);
        var service = CriarServico(repository.Object);

        var autenticado = await service.Authenticate(usuario.Email, senha);

        Assert.NotNull(autenticado);
        repository.Verify(item => item.AtualizarSenhaHash(
            usuario.Id,
            It.Is<string>(hash => hash != hashLegado && hash.Length > 48)), Times.Once);
    }

    [Fact]
    public async Task LoginComHashIdentityContinuaFuncionandoSemRegravarSenha()
    {
        const string senha = "senha-segura";
        var passwordHasher = new PasswordHasher<UsuarioModel>();
        var usuario = new UsuarioModel
        {
            Id = 5,
            Nome = "Autonomo",
            Email = "autonomo@example.com",
            TipoUsuario = TipoUsuario.AUTONOMO
        };
        usuario.Senha = passwordHasher.HashPassword(usuario, senha);

        var repository = new Mock<IUsuarioRepository>();
        repository.Setup(item => item.ListarUsuarioPorLogin(usuario.Email)).ReturnsAsync(usuario);
        var service = new AuthService(repository.Object, Configuration, passwordHasher);

        var autenticado = await service.Authenticate(usuario.Email, senha);

        Assert.Same(usuario, autenticado);
        repository.Verify(
            item => item.AtualizarSenhaHash(It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    private static AuthService CriarServico(IUsuarioRepository repository)
    {
        return new AuthService(
            repository,
            Configuration,
            new PasswordHasher<UsuarioModel>());
    }

    private static string CriarHashLegado(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(
            senha,
            salt,
            10_000,
            HashAlgorithmName.SHA256);
        return Convert.ToBase64String(salt.Concat(pbkdf2.GetBytes(32)).ToArray());
    }
}
