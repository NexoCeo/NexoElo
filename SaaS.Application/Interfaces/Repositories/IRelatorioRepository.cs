using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories;

public interface IRelatorioRepository
{
    Task<RelatorioMensalModel> ObterRelatorioMensalAsync(
        int usuarioId,
        int ano,
        int mes);
}
