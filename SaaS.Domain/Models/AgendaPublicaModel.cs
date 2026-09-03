using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class AgendaPublicaModel
    {
        public int UsuarioId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public TipoUsuario TipoUsuario { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string? FotoPerfil { get; set; }
        public string UrlAgendamento { get; set; } = string.Empty;
        public List<ServicoModel> Servicos { get; set; } = new();
    }
}
