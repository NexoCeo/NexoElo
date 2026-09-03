using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services
{
    public interface IStripePaymentService
    {
        Task<StripeCheckoutResponseModel> CriarCheckoutAssinatura(CriarCheckoutAssinaturaModel model);
        Task<StripePortalResponseModel> CriarPortalAssinatura(CriarPortalAssinaturaModel model);
        Task<StripeCancelamentoResponseModel> CancelarAssinatura(int usuarioId, CancelarAssinaturaModel model);
        Task ProcessarWebhook(string json, string stripeSignature);
    }
}
