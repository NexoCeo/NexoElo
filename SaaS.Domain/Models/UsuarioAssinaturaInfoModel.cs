using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class UsuarioAssinaturaInfoModel
    {
        public int UsuarioId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Email { get; set; }
        public StatusAssinatura Status { get; set; } = StatusAssinatura.NAO_ATIVA;
        public DateTime? DataFimAssinatura { get; set; }
        public string? StripeCustomerId { get; set; }
        public string? StripeSubscriptionId { get; set; }
        public string? StripePriceId { get; set; }
    }
}
