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

    private async Task VerificarPersistenciaAsync()
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT to_regclass('public.assinatura_recursos_ativos') IS NOT NULL", conn);
        if (!Convert.ToBoolean(await cmd.ExecuteScalarAsync()))
            throw new InvalidOperationException("A migracao dos recursos de assinatura ainda nao foi aplicada.");
    }

    public async Task<List<RecursoAssinatura>> ListarRecursosAsync(int usuarioId, bool empresa)
    {
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync();
        var itens = new List<RecursoAssinatura>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT S.ID_SERVICO, S.NOME_SERVICO
            FROM SERVICOS S WHERE S.USUARIO_FK=@id ORDER BY S.NOME_SERVICO, S.ID_SERVICO",conn))
        {
            cmd.Parameters.AddWithValue("id",usuarioId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                itens.Add(new RecursoAssinatura("SERVICO",reader.GetInt32(0),reader.GetString(1)));
        }
        if (empresa)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT DISTINCT U.ID_USUARIO, U.NOME_USUARIO
                FROM VINCULOS V JOIN USUARIOS U ON U.ID_USUARIO=V.PROFISSIONAL_FK
                WHERE V.EMPRESA_FK=@id AND V.STATUS_SOLICITACAO='APROVADO'
                ORDER BY U.NOME_USUARIO, U.ID_USUARIO",conn);
            cmd.Parameters.AddWithValue("id",usuarioId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                itens.Add(new RecursoAssinatura("PROFISSIONAL",reader.GetInt32(0),reader.GetString(1)));
        }
        return itens;
    }

    public async Task ValidarSelecaoAsync(int usuarioId, bool empresa, string plano,
        List<int>? servicos, List<int>? profissionais)
    {
        await VerificarPersistenciaAsync();
        var limiteS = plano switch { "BASICO" => 4, "INTERMEDIARIO" => 8, _ => int.MaxValue };
        var limiteP = plano switch { "BASICO" => 2, "INTERMEDIARIO" => 4, _ => int.MaxValue };
        var itens = await ListarRecursosAsync(usuarioId, empresa);
        Validar(itens.Where(x=>x.Tipo=="SERVICO").Select(x=>x.Id).ToList(),servicos,limiteS,"servicos");
        Validar(itens.Where(x=>x.Tipo=="PROFISSIONAL").Select(x=>x.Id).ToList(),profissionais,limiteP,"profissionais");
    }

    private static void Validar(List<int> existentes, List<int>? escolha, int limite, string tipo)
    {
        if (existentes.Count <= limite)
        {
            if (escolha != null && (escolha.Distinct().Count()!=escolha.Count || escolha.Except(existentes).Any()))
                throw new ArgumentException($"Selecao invalida de {tipo}.");
            return;
        }
        if (escolha == null || escolha.Count != limite ||
            escolha.Distinct().Count()!=escolha.Count || escolha.Except(existentes).Any())
            throw new ArgumentException($"Selecione exatamente {limite} {tipo} que permanecerao ativos.");
    }

    public async Task SalvarSelecaoAsync(int usuarioId, bool empresa, string plano,
        List<int>? servicos, List<int>? profissionais)
    {
        var itens = await ListarRecursosAsync(usuarioId,empresa);
        var limiteS = plano switch { "BASICO" => 4, "INTERMEDIARIO" => 8, _ => int.MaxValue };
        var limiteP = plano switch { "BASICO" => 2, "INTERMEDIARIO" => 4, _ => int.MaxValue };
        await using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var (tipo, limite, escolha) in new[] {
            ("SERVICO",limiteS,servicos), ("PROFISSIONAL",limiteP,profissionais) })
        {
            await using(var del = new NpgsqlCommand(
                "DELETE FROM ASSINATURA_RECURSOS_ATIVOS WHERE USUARIO_FK=@id AND TIPO_RECURSO=@tipo",conn,tx))
            {
                del.Parameters.AddWithValue("id",usuarioId);
                del.Parameters.AddWithValue("tipo",tipo);
                await del.ExecuteNonQueryAsync();
            }
            var existentes = itens.Where(x=>x.Tipo==tipo).Select(x=>x.Id).ToList();
            // Sem limite excedido, nao cria selecao restritiva.
            if (existentes.Count <= limite) continue;
            foreach (var id in escolha ?? Enumerable.Empty<int>())
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO ASSINATURA_RECURSOS_ATIVOS (USUARIO_FK,TIPO_RECURSO,RECURSO_ID) VALUES (@uid,@tipo,@rid)",conn,tx);
                cmd.Parameters.AddWithValue("uid",usuarioId);
                cmd.Parameters.AddWithValue("tipo",tipo);
                cmd.Parameters.AddWithValue("rid",id);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
    }

    // Permissao efetiva avaliada por requisicao, impedindo bypass por API publica.
    public async Task<bool> RecursoAtivoAsync(int usuarioId, string tipo, int recursoId)
    {
        var plano=await ConsultarAsync(usuarioId);
        if (!plano.AcessoAtivo) return false;
        var limite=tipo=="SERVICO"?plano.LimiteServicos:plano.LimiteProfissionais;
        if (limite==int.MaxValue) return true;
        var total=tipo=="SERVICO"?await ContarServicosAsync(usuarioId):await ContarProfissionaisAsync(usuarioId);
        if (total<=limite) return true;
        await using var conn=new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM ASSINATURA_RECURSOS_ATIVOS WHERE USUARIO_FK=@u AND TIPO_RECURSO=@t AND RECURSO_ID=@r)",conn);
        cmd.Parameters.AddWithValue("u",usuarioId);
        cmd.Parameters.AddWithValue("t",tipo);
        cmd.Parameters.AddWithValue("r",recursoId);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync());
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

public sealed record RecursoAssinatura(string Tipo, int Id, string Nome);
