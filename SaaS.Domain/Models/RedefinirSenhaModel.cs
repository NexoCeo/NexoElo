namespace SaaS.Domain.Models;

public class RedefinirSenhaModel
{
    public string TokenTemporario { get; set; } = string.Empty;
    public string NovaSenha { get; set; } = string.Empty;
}
