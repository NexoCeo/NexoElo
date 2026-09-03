using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IFuncionamentoRepository
    {
        Task<FuncionamentoConfigModel?> ObterFuncionamento(int usuarioId);
        Task<FuncionamentoConfigModel> SalvarFuncionamento(FuncionamentoConfigModel funcionamento);
    }
}
