using SaaS.Domain.Models;
using SaaS.Domain.Enums;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IAssinaturaRepository
    {
        Task<StatusAssinatura> ObterStatusAssinatura(int usuarioId);
        Task<UsuarioAssinaturaInfoModel?> ObterAssinaturaUsuario(int usuarioId);
        Task<bool> AtualizarStatusAssinatura(int usuarioId, StatusAssinatura novoStatus);
        Task<bool> AtualizarClienteStripe(int usuarioId, string stripeCustomerId);
        Task<bool> AtualizarAssinaturaStripe(
            int usuarioId,
            StatusAssinatura status,
            string? stripeCustomerId,
            string? stripeSubscriptionId,
            string? stripePriceId,
            DateTime? dataFimAssinatura);
        Task<bool> AtualizarAssinaturaPorStripeSubscriptionId(
            string stripeSubscriptionId,
            StatusAssinatura status,
            string? stripeCustomerId,
            string? stripePriceId,
            DateTime? dataFimAssinatura);
    }
}
