using Npgsql;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;

namespace SaaS.Api.Services;

// Regras de acesso nunca dependem de um plano enviado pelo navegador.
public sealed class PlanoAcessoService
{
    private readonly IAssinaturaRepository _assinaturas;
    private readonly IConfiguration _config;

    public PlanoAcessoService(IAssinaturaRepository assinaturas, IConfiguration config)
    {
        _assinaturas = assinaturas;
        _config = config;
    }

    public async Task<PlanoAcesso> ConsultarAsync(int usuarioId)
    {
        var registro = await _assinaturas.ObterAssinaturaUsuario(usuarioId)
            ?? throw new KeyNotFoundException("Usuario nao encontrado.");

        var inicio = registro.DataCriacaoUsuario ?? registro.DataInicioAssinatura ?? DateTime.UtcNow;
        var fimGratis = inicio.AddDays(30);
        var agora = DateTime.UtcNow;
        var pago = (registro.Status is StatusAssinatura.ATIVA or StatusAssinatura.CANCELAMENTO_PENDENTE)
            && (!registro.DataFimAssinatura.HasValue || registro.DataFimAssinatura.Value > agora);
        // O período grátis não é reiniciado ao tentar criar um checkout.
        var gratis = !pago && string.IsNullOrEmpty(registro.StripeSubscriptionId) && agora < fimGratis;
        var plano = pago ? ResolverPlano(registro.StripePriceId) : gratis ? "GRATIS" : "SEM_PLANO";
        var habilitado = (pago && plano != "SEM_PLANO") || gratis;
        return new PlanoAcesso(
            plano, habilitado, plano is "GRATIS" or "INTERMEDIARIO" or "AVANCADO",
            plano switch { "BASICO" => 4, "INTERMEDIARIO" => 8, _ => int.MaxValue },
            plano switch { "BASICO" => 2, "INTERMEDIARIO" => 4, _ => int.MaxValue },
            gratis ? fimGratis : registro.DataFimAssinatura, registro.Status.ToString());
    }

    private string ResolverPlano(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return "SEM_PLANO";
        foreach (var plano in new[] { "BASICO", "INTERMEDIARIO", "AVANCADO" })
            if (string.Equals(_config[$"Stripe:Prices:{plano}"], priceId, StringComparison.Ordinal))
                return plano;
        return "SEM_PLANO";
    }

    public async Task<int> ContarServicosAsync(int usuarioId)
    {
        await using var connection = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM SERVICOS WHERE USUARIO_FK=@id", connection);
        cmd.Parameters.AddWithValue("id", usuarioId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> ContarProfissionaisAsync(int empresaId)
    {
        await using var connection = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(DISTINCT PROFISSIONAL_FK) FROM VINCULOS WHERE EMPRESA_FK=@id AND STATUS_SOLICITACAO='APROVADO'", connection);
        cmd.Parameters.AddWithValue("id", empresaId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}

public sealed record PlanoAcesso(
    string Plano, bool AcessoAtivo, bool RelatoriosLiberados,
    int LimiteServicos, int LimiteProfissionais, DateTime? DataFim, string Status);
