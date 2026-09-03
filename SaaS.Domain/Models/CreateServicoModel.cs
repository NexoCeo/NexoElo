using Microsoft.AspNetCore.Http;

namespace SaaS.Domain.Models
{
    public class CreateServicoModel
    {
        public string NomeServico { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public int TempoEstimadoMinutos { get; set; } = 30;
        public IFormFile? ImagemServico { get; set; }
    }
}
