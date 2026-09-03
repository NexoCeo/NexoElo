using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services
{
    public interface IGeocodingService
    {
        Task<EnderecoGeocodificadoModel?> ObterEnderecoAsync(
            CoordenadasModel coordenadas,
            CancellationToken cancellationToken = default);
    }
}
