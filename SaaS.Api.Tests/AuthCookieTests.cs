using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Api.Middleware;
using SaaS.Api.Security;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public sealed class AuthCookieTests
{
    [Fact]
    public async Task LoginStoresTokenInHttpOnlyCookieWithoutReturningItInBody()
    {
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.Authenticate("empresa@example.com", "senha-segura"))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 7,
                Nome = "Empresa",
                Email = "empresa@example.com",
                TipoUsuario = TipoUsuario.EMPRESA
            });
        authService
            .Setup(service => service.GenerateJwtToken(It.IsAny<UsuarioModel>()))
            .ReturnsAsync("jwt-token");
        var controller = new AuthController(authService.Object, Mock.Of<IVinculoRepository>())
        {
            ControllerContext = CreateHttpsContext()
        };

        var result = await controller.Login(new AuthUsuarioModel
        {
            Email = "empresa@example.com",
            Senha = "senha-segura"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Null(ok.Value!.GetType().GetProperty("token"));
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{AuthCookie.Name}=jwt-token", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieAuthenticatedWriteRejectsUnknownOrigin()
    {
        var nextCalled = false;
        var middleware = new AuthenticatedCookieOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            ["https://meet-test-alpha.vercel.app"]);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie = $"{AuthCookie.Name}=jwt-token";
        context.Request.Headers.Origin = "https://malicious.example";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task CookieAuthenticatedWriteAcceptsConfiguredOrigin()
    {
        var nextCalled = false;
        var middleware = new AuthenticatedCookieOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            ["https://meet-test-alpha.vercel.app"]);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie = $"{AuthCookie.Name}=jwt-token";
        context.Request.Headers.Origin = "https://meet-test-alpha.vercel.app";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static ControllerContext CreateHttpsContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        return new ControllerContext { HttpContext = context };
    }
}
