namespace SaaS.Domain.Models;

public sealed class ArquivoUploadModel
{
    public required string Caminho { get; init; }
    public required string TipoConteudo { get; init; }
    public required byte[] Conteudo { get; init; }
}
