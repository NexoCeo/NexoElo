using Microsoft.AspNetCore.Http;

namespace SaaS.Domain.Models
{
    public class UpdatePerfilModel
    {
        public string Nome { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public IFormFile? FotoPerfil { get; set; }
    }
}
