namespace SaaS.Domain.Models
{
    public class EnderecoGeocodificadoModel
    {
        public List<string> Paises { get; init; } = [];
        public List<string> Estados { get; init; } = [];
        public List<string> Cidades { get; init; } = [];
    }
}
