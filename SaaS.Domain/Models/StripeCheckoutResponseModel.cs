using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class StripeCheckoutResponseModel
    {
        public string SessionId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public StatusAssinatura Status { get; set; } = StatusAssinatura.PENDENTE;
    }
}
