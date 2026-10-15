using SaaS.Api.Security;
using SaaS.Api.Services;

namespace SaaS.Api.Middleware;

/// <summary>
/// Bloqueia mutacoes operacionais de empresas e autonomos sem assinatura.
/// Rotas de pagamento e autenticacao permanecem disponiveis.
/// </summary>
public sealed class AssinaturaOperacionalMiddleware
{
    private readonly RequestDelegate _next;
    public AssinaturaOperacionalMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, PlanoAcessoService planos)
    {
        var request = context.Request;
        if (context.User.Identity?.IsAuthenticated == true &&
            (context.User.IsInRole("EMPRESA") || context.User.IsInRole("AUTONOMO")) &&
            request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !HttpMethods.IsGet(request.Method) &&
            !HttpMethods.IsHead(request.Method) &&
            !HttpMethods.IsOptions(request.Method) &&
            !request.Path.StartsWithSegments("/api/Assinatura", StringComparison.OrdinalIgnoreCase) &&
            !request.Path.StartsWithSegments("/api/Auth", StringComparison.OrdinalIgnoreCase) &&
            !request.Path.StartsWithSegments("/api/ForgotPassword", StringComparison.OrdinalIgnoreCase) &&
            context.User.TryGetUsuarioId(out var usuarioId))
        {
            var acesso = await planos.ConsultarAsync(usuarioId);
            if (!acesso.AcessoAtivo)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new {
                    erro = "Assinatura expirada. Contrate um plano para continuar.",
                    codigo = "ASSINATURA_INATIVA"
                });
                return;
            }
        }
        await _next(context);
    }
}
