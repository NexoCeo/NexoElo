using System.ComponentModel.DataAnnotations.Schema;

namespace SaaS.Domain.Models
{
    [Table("SERVICOS")]
    public class ServicoModel
    {
        public int Id { get; set; }
        public int? UsuarioFk { get; set; }
        public int? ProfissionalId { get; set; }
        public int? EmpresaId { get; set; }
        public string NomeServico { get; set; }
        public decimal Valor { get; set; }
        public int TempoEstimadoMinutos { get; set; } = 30;
        public string? ImagemServico { get; set; }
    }
}

