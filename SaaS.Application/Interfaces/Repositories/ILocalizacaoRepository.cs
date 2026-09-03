using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface ILocalizacaoRepository
    {
        Task<List<PaisModel>> ListarPaises();
        Task<List<EstadoModel>> ListarEstadosPorPais(int paisId);
        Task<List<CidadeModel>> ListarCidadesPorEstado(int estadoId);
        Task<bool> CidadeExiste(int cidadeId);
        Task<LocalizacaoResolvidaModel?> ResolverLocalizacaoAdministrativa(
            EnderecoGeocodificadoModel endereco);
    }
}
