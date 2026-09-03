using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Services
{
    public sealed class LocalizacaoCadastroService : ILocalizacaoCadastroService
    {
        private readonly IGeocodingService _geocodingService;
        private readonly ILocalizacaoRepository _localizacaoRepository;

        public LocalizacaoCadastroService(
            IGeocodingService geocodingService,
            ILocalizacaoRepository localizacaoRepository)
        {
            _geocodingService = geocodingService;
            _localizacaoRepository = localizacaoRepository;
        }

        public async Task<LocalizacaoResolvidaModel> ResolverAutomaticamenteAsync(
            CoordenadasModel coordenadas,
            CancellationToken cancellationToken = default)
        {
            ValidarCoordenadas(coordenadas);

            var endereco = await _geocodingService.ObterEnderecoAsync(coordenadas, cancellationToken);
            if (endereco == null)
            {
                throw new ArgumentException(
                    "Nao foi possivel identificar a cidade pelas coordenadas informadas.");
            }

            var localizacao = await _localizacaoRepository.ResolverLocalizacaoAdministrativa(endereco);
            return localizacao ?? throw new ArgumentException(
                "A cidade identificada nao existe no cadastro de regioes.");
        }

        private static void ValidarCoordenadas(CoordenadasModel coordenadas)
        {
            if (coordenadas.Latitude is < -90 or > 90)
                throw new ArgumentException("Latitude deve estar entre -90 e 90.");

            if (coordenadas.Longitude is < -180 or > 180)
                throw new ArgumentException("Longitude deve estar entre -180 e 180.");
        }
    }
}
