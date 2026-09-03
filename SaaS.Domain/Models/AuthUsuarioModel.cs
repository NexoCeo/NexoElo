namespace SaaS.Domain.Models
{
    public class AuthUsuarioModel
    {
        public string? Login { get; set; }
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public required string Senha { get; set; }
    }
}

