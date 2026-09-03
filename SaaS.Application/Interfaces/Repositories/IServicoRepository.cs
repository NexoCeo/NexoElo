using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IServicoRepository
    {
        Task<ServicoModel> InserirServico(ServicoModel servico);
        Task<ServicoModel?> AtualizarServico(
            int servicoId,
            int usuarioId,
            ServicoModel servico,
            bool atualizarImagem);
        Task<List<ServicoModel>> ListarServicos(int id);
    }
}

