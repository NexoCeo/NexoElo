namespace SaaS.Domain.Models
{
    public class EstadoModel
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public int PaisFk { get; set; }
    }
}
