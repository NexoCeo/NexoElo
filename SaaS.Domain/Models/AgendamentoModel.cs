using System.ComponentModel.DataAnnotations.Schema;

namespace SaaS.Domain.Models
{
    [Table("AGENDAMENTOS")]
    public class AgendamentoModel
    {
        public int Id { get; set; }
        public int ClienteId { get; set; }
        public int? ProfissionalId { get; set; }
        public int? EmpresaId { get; set; }
        public int ServicoId { get; set; }
        public List<int> ServicoIds { get; set; } = new();
        public List<AtendimentoProfissionalModel> Atendimentos { get; set; } = new();
        public DateTime DataAgendamento { get; set; }
        public decimal Valor { get; set; }
        public decimal Taxa { get; set; }
        public decimal ValorServico { get; set; }
        public int DuracaoMinutos { get; set; }
        public string Status { get; set; } = "AGENDADO";
        [NotMapped]
        public bool StatusAlterado { get; set; }
        public string? MotivoCancelamento { get; set; }
        public string ClienteNome { get; set; } = string.Empty;
        public string ServicoNome { get; set; } = string.Empty;
        public string ProfissionalNome { get; set; } = string.Empty;
        public List<ProfissionalResumoAgendamentoModel> Profissionais { get; set; } = new();
    }
}


namespace SaaS.Domain.Models
{
    public sealed class ProfissionalResumoAgendamentoModel
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? FotoPerfil { get; set; }
    }
}
