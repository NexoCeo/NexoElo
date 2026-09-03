using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services
{
    public interface IAuthService
    {
        Task<UsuarioModel?> Authenticate(string login, string senha);
        Task<string> GenerateJwtToken(UsuarioModel usuarioModel);
    }
}

