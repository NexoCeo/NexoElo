using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class StripeCancelamentoResponseModel
    {
        public string StripeSubscriptionId { get; set; } = string.Empty;
        public string StripeStatus { get; set; } = string.Empty;
        public StatusAssinatura Status { get; set; } = StatusAssinatura.PENDENTE;
        public bool CancelAtPeriodEnd { get; set; }
        public DateTime? DataFimAssinatura { get; set; }
    }
}
