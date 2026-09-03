using SaaS.Domain.Models;

namespace SaaS.Application.Interfaces.Repositories
{
    public interface IUsuarioRepository
    {
        Task<UsuarioModel> InserirUsuario(UsuarioModel usuarioModel, int? empresaProfissionalId = null, string statusVinculoProfissional = "APROVADO");
        Task<UsuarioModel> InserirUsuario(
            UsuarioModel usuarioModel,
            int? empresaProfissionalId,
            string statusVinculoProfissional,
            CoordenadasModel? coordenadas);
        Task<IEnumerable<UsuarioModel>> ListarUsuarios();
        Task<UsuarioModel?> ListarUsuarioPorId(int usuarioId);
        Task<UsuarioModel?> ListarUsuarioPorSlug(string slug);
        Task<int> GerarSlugsPendentes();
        Task<bool> AtualizarDadosUsuario(int usuarioId, string? novoNome, string? novoEmail, string? novaSenha);
        Task<UsuarioModel?> AtualizarPerfilUsuario(
            int usuarioId,
            string nome,
            string email,
            string? telefone,
            string? fotoPerfil,
            bool atualizarFoto);
        Task AtualizarSenhaHash(int usuarioId, string senhaHash);
        Task<UsuarioModel?> ListarUsuarioPorLogin(string login);
        Task<UsuarioModel?> ListarUsuarioPorEmail(string email);
        Task<string?> ObterStatusVinculoProfissionalEmpresa(int profissionalId, int empresaId);
        Task<bool> AtualizarAcessoProfissionalEmpresa(
            int profissionalId,
            int empresaId,
            string novaSenha);
        Task RestaurarAcessoProfissionalEmpresa(
            int profissionalId,
            int empresaId,
            string senhaHashAnterior,
            string statusAnterior);
    }
}

