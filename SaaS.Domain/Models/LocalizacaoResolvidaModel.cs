namespace SaaS.Domain.Models
{
    public class LocalizacaoResolvidaModel
    {
        public int PaisId { get; init; }
        public string PaisNome { get; init; } = string.Empty;
        public int EstadoId { get; init; }
        public string EstadoNome { get; init; } = string.Empty;
        public int CidadeId { get; init; }
        public string CidadeNome { get; init; } = string.Empty;
    }
}
