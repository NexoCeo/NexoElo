using System.Security.Claims;

namespace SaaS.Api.Security;

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUsuarioId(this ClaimsPrincipal principal, out int usuarioId)
    {
        return int.TryParse(
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out usuarioId);
    }

    public static bool EhProprioUsuario(this ClaimsPrincipal principal, int usuarioId)
    {
        return principal.TryGetUsuarioId(out var usuarioAutenticadoId) &&
               usuarioAutenticadoId == usuarioId;
    }
}
