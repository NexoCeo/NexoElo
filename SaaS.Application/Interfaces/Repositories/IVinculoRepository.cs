using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IVinculoRepository
    {
        Task<bool> JaExisteSolicitacaoPendente(int profissionalId, int empresaId);
        Task CriarSolicitacaoVinculo(int profissionalId, int empresaId);
        Task<List<ProfissionalEmpresaModel>> ListarProfissionaisPorEmpresa(int empresaId);
        Task<List<ServicoModel>> ListarServicosDoProfissional(int profissionalId, int empresaId);
        Task<List<ServicoModel>> VincularServicosProfissional(int profissionalId, int empresaId, IEnumerable<int> servicoIds);

        Task AtualizarStatusSolicitacaoAsync(int id, string status);
        Task<(int ProfissionalId, int EmpresaId)> ObterIdsDaSolicitacaoAsync(int id);

        Task<string?> ObterStatusSolicitacaoPorProfissionalAsync(int profissionalId);
        Task<IEnumerable<SolicitacaoVinculoModel>> ListarSolicitacoesPendentesPorEmpresa(
        int empresaId);

        Task<bool> PossuiVinculoAprovadoAsync(int profissionalId);

        Task<bool> ResponderSolicitacaoAsync(
            int solicitacaoId,
            int empresaId,
            string status);

        Task<VinculoProfissionalModel?> ObterVinculoAtualDoProfissionalAsync(
            int profissionalId);

    }
}
