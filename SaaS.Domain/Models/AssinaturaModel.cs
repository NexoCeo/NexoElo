using System.ComponentModel.DataAnnotations.Schema;
using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    [Table("ASSINATURAS")]
    public class AssinaturaModel
    {
        [Column("ID_ASSINATURA")]
        public int Id { get; set; }

        [Column("USUARIO_FK")]
        public int UsuarioFk { get; set; }

        [Column("STATUS_ASSINATURA")]
        public StatusAssinatura Status { get; set; } = StatusAssinatura.NAO_ATIVA;

        [Column("DATA_INICIO_ASSINATURA")]
        public DateTime DataInicio { get; set; }

        [Column("DATA_FIM_ASSINATURA")]
        public DateTime? DataFim { get; set; }

        [Column("STRIPE_CUSTOMER_ID")]
        public string? StripeCustomerId { get; set; }

        [Column("STRIPE_SUBSCRIPTION_ID")]
        public string? StripeSubscriptionId { get; set; }

        [Column("STRIPE_PRICE_ID")]
        public string? StripePriceId { get; set; }

        [Column("DATA_CRIACAO_ASSINATURA")]
        public DateTime DataCriacao { get; set; }

        [Column("DATA_ALTERACAO_ASSINATURA")]
        public DateTime DataAlteracao { get; set; }
    }
}
