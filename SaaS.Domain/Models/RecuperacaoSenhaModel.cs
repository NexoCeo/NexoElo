namespace SaaS.Domain.Models
{
    public class RecuperacaoSenhaModel
    {
        public int Id { get; set; }
        public int UsuarioFk { get; set; }
        public string Codigo { get; set; } = string.Empty;
        public DateTime DataExpiracao { get; set; }
        public bool Usado { get; set; } = false;
    }
}

