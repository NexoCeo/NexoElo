using SaaS.Domain.Models;
using SaaS.Domain.Enums;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IAgendamentoRepository
    {
        Task<AgendamentoModel> CriarAgendamentoAsync(AgendamentoModel agendamento);
        Task<List<HorarioDisponivelModel>> ListarHorariosDisponiveis(
            int responsavelId,
            int? profissionalId,
            int servicoId,
            DateTime data);
        Task<List<AgendamentoModel>> ListarAgendamentosDoDia(int usuarioId);
        Task<List<AgendamentoModel>> ListarAgendamentosPorData(int usuarioId, DateTime data, int? profissionalId = null);
        Task<List<AgendamentoModel>> ListarHistoricoAgendamentos(int usuarioId);
        Task<List<AgendamentoModel>> ListarAgendamentosPorPeriodo(int usuarioId, DateTime inicio, DateTime fim, int? profissionalId = null);
        Task<TipoUsuario> ObterTipoUsuarioAsync(int responsavelId);
        Task<AgendamentoModel> AtualizarStatusAsync(int agendamentoId, int usuarioId, string status);
        Task<int> ConcluirAgendamentosDoDiaAsync(int usuarioId);
    }
}


