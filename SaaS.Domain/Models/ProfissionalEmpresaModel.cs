namespace SaaS.Domain.Models
{
    public class ProfissionalEmpresaModel
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Telefone { get; set; }
        public string? FotoPerfil { get; set; }
        public int? CidadeFk { get; set; }
        public string VinculoStatus { get; set; } = "NENHUM_VINCULO";
        public DateTime? DataSolicitacao { get; set; }
        public List<ServicoModel> Servicos { get; set; } = new();
    }
}
