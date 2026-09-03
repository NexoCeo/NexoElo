using Microsoft.AspNetCore.Http;
using Moq;
using SaaS.Api.Middleware;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public sealed class MissingUploadFallbackMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsStoredUploadWithoutReplacingItsContent()
    {
        var upload = new ArquivoUploadModel
        {
            Caminho = "uploads/service.png",
            TipoConteudo = "image/png",
            Conteudo = [0x89, 0x50, 0x4E, 0x47]
        };
        var service = new Mock<IArquivoUploadService>();
        service
            .Setup(item => item.ObterAsync(
                upload.Caminho,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(upload);
        var nextCalled = false;
        var middleware = new MissingUploadFallbackMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/uploads/service.png";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, service.Object);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(upload.TipoConteudo, context.Response.ContentType);
        Assert.Equal(upload.Conteudo.Length, context.Response.ContentLength);
        Assert.Equal(upload.Conteudo, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task InvokeAsync_ContinuesWithNotFoundForMissingUpload()
    {
        var service = new Mock<IArquivoUploadService>();
        service
            .Setup(item => item.ObterAsync(
                "uploads/missing-profile.png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArquivoUploadModel?)null);
        var nextCalled = false;
        var middleware = new MissingUploadFallbackMiddleware(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/uploads/missing-profile.png";

        await middleware.InvokeAsync(context, service.Object);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ContinuesForNonImageRequest()
    {
        var service = new Mock<IArquivoUploadService>();
        var nextCalled = false;
        var middleware = new MissingUploadFallbackMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/usuarios";

        await middleware.InvokeAsync(context, service.Object);

        Assert.True(nextCalled);
        service.Verify(item => item.ObterAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
