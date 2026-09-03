namespace SaaS.Domain.Models
{
    public class CidadeModel
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public int EstadoFk { get; set; }
    }
}
