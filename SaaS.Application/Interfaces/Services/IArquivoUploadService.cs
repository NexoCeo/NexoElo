using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services;

public interface IArquivoUploadService
{
    Task GarantirEstruturaAsync(CancellationToken cancellationToken = default);

    Task<string> SalvarAsync(
        byte[] conteudo,
        string extensao,
        string tipoConteudo,
        CancellationToken cancellationToken = default);

    Task<ArquivoUploadModel?> ObterAsync(
        string caminho,
        CancellationToken cancellationToken = default);

    Task RemoverAsync(
        string? caminho,
        CancellationToken cancellationToken = default);
}
