using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IRecuperacaoSenhaRepository
    {
        Task<RecuperacaoSenhaModel?> ValidarCodigoAsync(int usuarioId, string codigoHash);
        Task RegistrarTentativaInvalidaAsync(int usuarioId);
        Task DefinirTokenResetAsync(int recuperacaoId, string tokenHash, DateTime dataExpiracao);
        Task<bool> RedefinirSenhaAsync(string tokenHash, string novaSenha);
    }
}

