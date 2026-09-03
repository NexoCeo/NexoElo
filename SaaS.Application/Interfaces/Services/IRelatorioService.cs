using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Services;

public interface IRelatorioService
{
    Task<RelatorioArquivoModel> GerarRelatorioAsync(
        int usuarioId,
        TipoRelatorio tipo,
        int ano,
        int mes);
}
