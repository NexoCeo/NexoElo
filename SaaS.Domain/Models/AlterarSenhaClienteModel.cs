namespace SaaS.Domain.Models;

public sealed class AlterarSenhaClienteModel
{
    public string SenhaAtual { get; set; } = string.Empty;
    public string ConfirmarSenhaAtual { get; set; } = string.Empty;
    public string NovaSenha { get; set; } = string.Empty;
}
