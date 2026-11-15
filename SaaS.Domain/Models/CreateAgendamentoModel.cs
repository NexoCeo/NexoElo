namespace SaaS.Domain.Models
{
    public class CreateAgendamentoModel
    {
        public int ClienteId { get; set; }
        public int ResponsavelId { get; set; }
        public int? ProfissionalId { get; set; }
        public int ServicoId { get; set; }
        public List<int>? ServicoIds { get; set; }
        public List<AtendimentoProfissionalModel>? Atendimentos { get; set; }
        public DateTime DataAgendamento { get; set; }
    }
}




namespace SaaS.Domain.Models
{
    public class AtendimentoProfissionalModel
    {
        public int ServicoId { get; set; }
        public int ProfissionalId { get; set; }
    }
}
