namespace SaaS.Application.Interfaces.Services
{
    public interface IEmailService
    {
        Task EnviarCodigoAsync(string destino, string codigo, string nomeUsuario);
        Task EnviarCredenciaisProfissionalAsync(
            string destino,
            string emailProfissional,
            string senhaTemporaria,
            string nomeProfissional);
    }
}
