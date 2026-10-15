namespace SaaS.Domain.Models
{
    public class CriarCheckoutAssinaturaModel
    {
        public int UsuarioId { get; set; }
        public string? PriceId { get; set; }
        public string? Plano { get; set; }
        public bool ReativarRenovacao { get; set; }
        public List<int>? ServicosAtivos { get; set; }
        public List<int>? ProfissionaisAtivos { get; set; }
        public string? SuccessUrl { get; set; }
        public string? CancelUrl { get; set; }
    }
}

