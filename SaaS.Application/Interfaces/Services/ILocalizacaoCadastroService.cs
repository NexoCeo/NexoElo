using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services
{
    public interface ILocalizacaoCadastroService
    {
        Task<LocalizacaoResolvidaModel> ResolverAutomaticamenteAsync(
            CoordenadasModel coordenadas,
            CancellationToken cancellationToken = default);
    }
}
