using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IForgotPasswordRepository
    {
        Task SalvarCodigoRecuperacao(int usuarioId, string codigoHash);
        Task InvalidarCodigosRecuperacao(int usuarioId);
        Task<UsuarioEmail?> ObterUsuarioIdPorEmail(string email);
    }
}

