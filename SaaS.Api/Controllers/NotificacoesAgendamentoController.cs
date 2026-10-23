using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SaaS.Api.Security;

namespace SaaS.Api.Controllers;

[ApiController]
[Route("api/notificacoes-agendamento")]
[Authorize(Roles = "EMPRESA,AUTONOMO,PROFISSIONAL")]
public class NotificacoesAgendamentoController : ControllerBase
{
    private readonly IConfiguration _configuration;
    public NotificacoesAgendamentoController(IConfiguration configuration) => _configuration = configuration;

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
        const string sql = @"SELECT ID, AGENDAMENTO_ID, TITULO, MENSAGEM, DATA_AGENDAMENTO, LIDA
          FROM NOTIFICACOES_AGENDAMENTO
          WHERE DESTINATARIO_USUARIO_ID = @uid
          ORDER BY CRIADA_EM DESC LIMIT 100";
        var result = new List<object>();
        await using var con = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await con.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@uid", usuarioId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new { Id = reader.GetInt64(0), AgendamentoId = reader.GetInt32(1),
                Titulo = reader.GetString(2), Mensagem = reader.GetString(3),
                DataAgendamento = reader.GetDateTime(4), Lida = reader.GetBoolean(5) });
        return Ok(result);
    }

    [HttpPut("{id:long}/lida")]
    public async Task<IActionResult> MarcarLida(long id)
    {
        if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
        await using var con = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await con.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"UPDATE NOTIFICACOES_AGENDAMENTO SET LIDA=TRUE
          WHERE ID=@id AND DESTINATARIO_USUARIO_ID=@uid", con);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", usuarioId);
        return await cmd.ExecuteNonQueryAsync() > 0 ? NoContent() : NotFound();
    }

    [HttpPut("marcar-todas-lidas")]
    public async Task<IActionResult> MarcarTodasLidas()
    {
        if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
        await using var con = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await con.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"UPDATE NOTIFICACOES_AGENDAMENTO SET LIDA=TRUE
          WHERE DESTINATARIO_USUARIO_ID=@uid AND LIDA=FALSE", con);
        cmd.Parameters.AddWithValue("@uid", usuarioId);
        await cmd.ExecuteNonQueryAsync();
        return NoContent();
    }
}
