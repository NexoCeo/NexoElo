namespace SaaS.Domain.Models
{
    public class PagamentoServicoModel
    {
        public int ClienteId { get; set; }
        public int ProfissionalId { get; set; }
        public int EmpresaId { get; set; }
        public decimal Valor { get; set; }
        public string Descricao { get; set; }
    }
}

