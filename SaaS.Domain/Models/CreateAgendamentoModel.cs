namespace SaaS.Domain.Models
{
    public class CreateAgendamentoModel
    {
        public int ClienteId { get; set; }
        public int ResponsavelId { get; set; }
        public int? ProfissionalId { get; set; }
        public int ServicoId { get; set; }
        public DateTime DataAgendamento { get; set; }
    }
}



