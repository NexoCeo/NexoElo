using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Http;
using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class CreateUsuarioModel
    {
        public string Nome { get; set; }
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? Slug { get; set; }
        public string? Senha { get; set; }
        public IFormFile FotoPerfil { get; set; }
        public TipoUsuario? TipoUsuario { get; set; }
        public StatusAssinatura? AssinaturaAtiva { get; set; }
        public DateTime? DataFimAssinatura { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAlteracao { get; set; }
        public int CidadeFk { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public int? EmpresaId { get; set; }
        public string? Cnpj { get; set; }
        public string? NomeFantasia { get; set; }
    }
}
