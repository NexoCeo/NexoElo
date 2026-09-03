using SaaS.Api.Security;

namespace SaaS.Api.Middleware;

public sealed class AuthenticatedCookieOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowedOrigins;

    public AuthenticatedCookieOriginMiddleware(
        RequestDelegate next,
        IEnumerable<string> allowedOrigins)
    {
        _next = next;
        _allowedOrigins = new HashSet<string>(
            allowedOrigins.Select(origin => origin.TrimEnd('/')),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresTrustedOrigin(context.Request) && !HasTrustedOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Origem da requisicao nao autorizada."
            });
            return;
        }

        await _next(context);
    }

    private static bool RequiresTrustedOrigin(HttpRequest request)
    {
        var safeMethod = HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method) ||
            HttpMethods.IsTrace(request.Method);

        return !safeMethod && request.Cookies.ContainsKey(AuthCookie.Name);
    }

    private bool HasTrustedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString().TrimEnd('/');
        return !string.IsNullOrWhiteSpace(origin) && _allowedOrigins.Contains(origin);
    }
}
