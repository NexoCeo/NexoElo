using Microsoft.AspNetCore.Http;

namespace SaaS.Domain.Models
{
    public class UpdateServicoModel
    {
        public string NomeServico { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public int TempoEstimadoMinutos { get; set; }
        public IFormFile? ImagemServico { get; set; }
    }
}
