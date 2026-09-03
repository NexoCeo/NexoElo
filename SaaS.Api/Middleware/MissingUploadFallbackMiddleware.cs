using SaaS.Application.Interfaces.Services;

namespace SaaS.Api.Middleware;

public sealed class MissingUploadFallbackMiddleware
{
    private static readonly HashSet<string> SupportedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly RequestDelegate _next;

    public MissingUploadFallbackMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IArquivoUploadService arquivoUploadService)
    {
        if (!EhRequisicaoDeImagem(context.Request))
        {
            await _next(context);
            return;
        }

        var caminho = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
        SaaS.Domain.Models.ArquivoUploadModel? arquivo;
        try
        {
            arquivo = await arquivoUploadService.ObterAsync(
                caminho,
                context.RequestAborted);
        }
        catch (ArgumentException)
        {
            await _next(context);
            return;
        }

        if (arquivo == null)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = arquivo.TipoConteudo;
        context.Response.ContentLength = arquivo.Conteudo.Length;
        context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(
                arquivo.Conteudo,
                context.RequestAborted);
    }

    private static bool EhRequisicaoDeImagem(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        if (!request.Path.StartsWithSegments("/uploads"))
            return false;

        var extension = Path.GetExtension(request.Path.Value) ?? string.Empty;
        return SupportedImageExtensions.Contains(extension);
    }
}
