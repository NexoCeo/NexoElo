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
        public DateTime DataAgendamento { get; set; }
        public decimal Valor { get; set; }
        public decimal Taxa { get; set; }
        public decimal ValorServico { get; set; }
        public string Status { get; set; } = "AGENDADO";
        public string ClienteNome { get; set; } = string.Empty;
        public string ServicoNome { get; set; } = string.Empty;
        public string ProfissionalNome { get; set; } = string.Empty;
    }
}

