using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IProfissionalRepository
    {
        Task<List<UsuarioModel>> ListarEmpresasMesmaCidade(int profissionalId);
        Task<IEnumerable<EmpresaCidadeModel>> ListarEmpresasPorCidade(int cidadeId);
    }
}


