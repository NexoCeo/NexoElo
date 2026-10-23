using Npgsql;
using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using SaaS.Api.Hubs;
using SaaS.Api.Security;
using SaaS.Api.Services;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AgendamentoController : ControllerBase
    {
        private readonly IAgendamentoRepository _agendamentoRepository;
        private readonly IVinculoRepository _vinculoRepository;
        private readonly IHubContext<AgendaHub> _agendaHub;
        private readonly ILogger<AgendamentoController> _logger;
        private readonly PlanoAcessoService? _planos;
        private readonly IUsuarioRepository? _usuarios;
        private readonly IConfiguration _configuration;

        public AgendamentoController(
            IAgendamentoRepository agendamentoRepository,
            IVinculoRepository vinculoRepository,
            IHubContext<AgendaHub> agendaHub,
            ILogger<AgendamentoController> logger,
            PlanoAcessoService? planos = null,
            IUsuarioRepository? usuarios = null, IConfiguration? configuration = null)
        {
            _agendamentoRepository = agendamentoRepository;
            _vinculoRepository = vinculoRepository;
            _agendaHub = agendaHub;
            _logger = logger;
            _planos = planos;
            _usuarios = usuarios;
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        // POST: api/Agendamento
        [HttpPost]
        [Authorize(Roles = "CLIENTE,EMPRESA,AUTONOMO,PROFISSIONAL")]
        public async Task<IActionResult> CriarAgendamento([FromBody] CreateAgendamentoModel criarAgendamento)
        {
            if (criarAgendamento == null)
                return BadRequest(new { erro = "Dados do agendamento invalidos." });

            if (criarAgendamento.ResponsavelId <= 0 ||
                criarAgendamento.ServicoId <= 0 ||
                criarAgendamento.DataAgendamento == default)
            {
                return BadRequest(new { erro = "Responsavel, servico, data e horario sao obrigatorios." });
            }

            if (!User.TryGetUsuarioId(out var clienteId))
                return Unauthorized();

            try
            {
                if (!User.IsInRole(nameof(TipoUsuario.CLIENTE)))
                {
                    if (User.IsInRole(nameof(TipoUsuario.PROFISSIONAL)))
                    {
                        var vinculo = await _vinculoRepository.ObterVinculoAtualDoProfissionalAsync(clienteId);
                        if (vinculo?.VinculoStatus != "APROVADO" ||
                            vinculo.EmpresaId != criarAgendamento.ResponsavelId ||
                            criarAgendamento.ProfissionalId != clienteId)
                            return Forbid();
                    }
                    else if (criarAgendamento.ResponsavelId != clienteId)
                        return Forbid();

                    if (criarAgendamento.ClienteId <= 0 || _usuarios == null)
                        return BadRequest(new { erro = "Selecione um cliente." });
                    var cliente = await _usuarios.ListarUsuarioPorId(criarAgendamento.ClienteId);
                    if (cliente?.TipoUsuario != TipoUsuario.CLIENTE)
                        return BadRequest(new { erro = "Cliente invalido." });
                    clienteId = cliente.Id;
                }

                var tipoUsuario = await _agendamentoRepository.ObterTipoUsuarioAsync(criarAgendamento.ResponsavelId);
                if (tipoUsuario == TipoUsuario.PROFISSIONAL)
                    return BadRequest(new { erro = "Profissional depende da empresa. Informe a empresa responsavel pelo agendamento." });

                if (tipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
                    return BadRequest(new { erro = "Apenas empresa ou autonomo podem receber agendamentos." });

                if (tipoUsuario == TipoUsuario.EMPRESA && criarAgendamento.ProfissionalId.GetValueOrDefault() <= 0)
                {
                    return BadRequest(new { erro = "Selecione um profissional vinculado a empresa." });
                }

                if (_planos != null)
                {
                    if (!await _planos.RecursoAtivoAsync(criarAgendamento.ResponsavelId, "SERVICO", criarAgendamento.ServicoId))
                        return Conflict(new { erro = "Este servico esta indisponivel no plano atual." });
                    if (tipoUsuario == TipoUsuario.EMPRESA &&
                        !await _planos.RecursoAtivoAsync(criarAgendamento.ResponsavelId, "PROFISSIONAL", criarAgendamento.ProfissionalId!.Value))
                        return Conflict(new { erro = "Este profissional esta indisponivel no plano atual." });
                }

                var novoAgendamento = new AgendamentoModel
                {
                    ClienteId = clienteId,
                    ServicoId = criarAgendamento.ServicoId,
                    DataAgendamento = criarAgendamento.DataAgendamento,
                    ProfissionalId = tipoUsuario == TipoUsuario.AUTONOMO
                        ? criarAgendamento.ResponsavelId
                        : criarAgendamento.ProfissionalId,
                    EmpresaId = tipoUsuario == TipoUsuario.EMPRESA ? criarAgendamento.ResponsavelId : (int?)null
                };

                var agendamentoCriado = await _agendamentoRepository.CriarAgendamentoAsync(novoAgendamento);
                if (User.IsInRole(nameof(TipoUsuario.CLIENTE)))
                {
                    try { await RegistrarNotificacoesClienteAsync(agendamentoCriado.Id); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Falha ao registrar notificacoes do agendamento {AgendamentoId}", agendamentoCriado.Id);
                    }
                }
                try
                {
                    await NotificarAgendaAtualizada(
                        agendamentoCriado,
                        criarAgendamento.ResponsavelId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Agendamento {AgendamentoId} criado, mas a notificacao em tempo real falhou.",
                        agendamentoCriado.Id);
                }

                return Ok(agendamentoCriado);
            }
            catch (AgendamentoIndisponivelException ex)
            {
                return Conflict(new
                {
                    erro = ex.Message,
                    SugestaoDataAgendamento = ex.SugestaoDataAgendamento
                });
            }
            catch (RegraAgendamentoException ex)
            {
                return Conflict(new { erro = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Nao foi possivel criar o agendamento." });
            }
        }

        [HttpGet("clientes")]
        [Authorize(Roles = "EMPRESA,AUTONOMO,PROFISSIONAL")]
        public async Task<IActionResult> ListarClientes([FromQuery] string? busca = null)
        {
            if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
            if (!await ProfissionalPodeAcessarAgenda(usuarioId)) return Forbid();
            // A listagem nunca deve retornar o cadastro global de clientes.
            var prestadorId = usuarioId;
            if (User.IsInRole(nameof(TipoUsuario.PROFISSIONAL)))
            {
                var vinculo = await _vinculoRepository.ObterVinculoAtualDoProfissionalAsync(usuarioId);
                if (vinculo?.VinculoStatus != "APROVADO") return Forbid();
                prestadorId = vinculo.EmpresaId;
            }
            var clientes = await _agendamentoRepository.ListarClientesDoPrestadorAsync(prestadorId, busca);
            return Ok(clientes.Select(c => new { c.Id, c.Nome }));
        }

        [HttpGet("horarios-disponiveis")]
        [AllowAnonymous]
        [EnableRateLimiting("public-read")]
        public async Task<IActionResult> ListarHorariosDisponiveis(
            [FromQuery] int responsavelId,
            [FromQuery] int? profissionalId,
            [FromQuery] int servicoId,
            [FromQuery] DateTime data)
        {
            if (responsavelId <= 0 || servicoId <= 0 || data == default)
                return BadRequest(new { erro = "Responsavel, servico e data sao obrigatorios." });

            try
            {
                if (_planos != null)
                {
                    if (!await _planos.RecursoAtivoAsync(responsavelId,"SERVICO",servicoId))
                        return Ok(Array.Empty<HorarioDisponivelModel>());
                    if (profissionalId.HasValue &&
                        !await _planos.RecursoAtivoAsync(responsavelId,"PROFISSIONAL",profissionalId.Value))
                        return Ok(Array.Empty<HorarioDisponivelModel>());
                }

                var horarios = await _agendamentoRepository.ListarHorariosDisponiveis(
                    responsavelId,
                    profissionalId,
                    servicoId,
                    data);

                return Ok(horarios);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Nao foi possivel carregar os horarios disponiveis." });
            }
        }

        [HttpGet("agenda/{usuarioId:int}")]
        [Authorize]
        public async Task<IActionResult> ListarAgendaPorData(
            int usuarioId,
            [FromQuery] DateTime data,
            [FromQuery] int? profissionalId = null)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            if (!await ProfissionalPodeAcessarAgenda(usuarioId))
                return Forbid();

            if (data == default)
                return BadRequest(new { erro = "Informe uma data valida." });

            try
            {
                var agendamentos = profissionalId.HasValue
                    ? await _agendamentoRepository.ListarAgendamentosPorData(
                        usuarioId,
                        data,
                        profissionalId)
                    : await _agendamentoRepository.ListarAgendamentosPorData(usuarioId, data);

                return Ok(agendamentos);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Nao foi possivel carregar a agenda." });
            }
        }

        [HttpGet("agenda/{usuarioId:int}/periodo")]
        [Authorize]
        public async Task<IActionResult> ListarAgendaPorPeriodo(
            int usuarioId,
            [FromQuery] DateTime inicio,
            [FromQuery] DateTime fim,
            [FromQuery] int? profissionalId = null)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            if (!await ProfissionalPodeAcessarAgenda(usuarioId))
                return Forbid();

            try
            {
                var agendamentos = await _agendamentoRepository.ListarAgendamentosPorPeriodo(
                    usuarioId,
                    inicio,
                    fim,
                    profissionalId);
                return Ok(agendamentos);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Nao foi possivel carregar o periodo da agenda." });
            }
        }

        // GET: api/agendamentos/dia?usuarioId=1&tipoUsuario=PROFISSIONAL
        [HttpGet("dia")]
        [Authorize]
        public async Task<IActionResult> ListarAgendamentosDoDia([FromQuery] int usuarioId)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            if (!await ProfissionalPodeAcessarAgenda(usuarioId))
                return Forbid();

            try
            {
                var agendamentos = await _agendamentoRepository.ListarAgendamentosDoDia(usuarioId);
                return Ok(agendamentos);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Nao foi possivel carregar os agendamentos." });
            }
        }

        [HttpGet("historico/{usuarioId}")]
        [Authorize]
        public async Task<IActionResult> GetHistoricoAgendamentos(int usuarioId)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            if (!await ProfissionalPodeAcessarAgenda(usuarioId))
                return Forbid();

            try
            {
                var agendamentos = await _agendamentoRepository.ListarHistoricoAgendamentos(usuarioId);
                return Ok(agendamentos);
            }
            catch (Exception)
            {
                return StatusCode(500, new { mensagem = "Nao foi possivel buscar o historico de agendamentos." });
            }
        }

        [HttpPatch("{agendamentoId:int}/status")]
        [Authorize(Roles = nameof(TipoUsuario.CLIENTE) + "," + nameof(TipoUsuario.EMPRESA) + "," + nameof(TipoUsuario.AUTONOMO))]
        public async Task<IActionResult> AtualizarStatus(
            int agendamentoId,
            [FromBody] AtualizarStatusAgendamentoModel model)
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Unauthorized();

            if (model == null || string.IsNullOrWhiteSpace(model.Status))
                return BadRequest(new { erro = "Informe o novo status do agendamento." });

            try
            {
                var agendamento = await _agendamentoRepository.AtualizarStatusAsync(
                    agendamentoId,
                    usuarioId,
                    model.Status);

                var responsavelId = agendamento.EmpresaId ?? agendamento.ProfissionalId;
                try
                {
                    if (responsavelId.GetValueOrDefault() > 0)
                        await NotificarAgendaAtualizada(agendamento, responsavelId!.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Agendamento {AgendamentoId} atualizado, mas a notificacao em tempo real falhou.",
                        agendamento.Id);
                }

                return Ok(agendamento);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (RegraAgendamentoException ex)
            {
                return Conflict(new { erro = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Não foi possível atualizar o agendamento." });
            }
        }

        [HttpPatch("status/concluir-dia-atual")]
        [Authorize(Roles = nameof(TipoUsuario.EMPRESA) + "," + nameof(TipoUsuario.AUTONOMO))]
        public async Task<IActionResult> ConcluirAgendamentosDoDiaAtual()
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Unauthorized();

            try
            {
                var quantidade = await _agendamentoRepository.ConcluirAgendamentosDoDiaAsync(usuarioId);

                try
                {
                    await _agendaHub.Clients
                        .Group(AgendaHub.GrupoUsuario(usuarioId))
                        .SendAsync(AgendaHub.EventoAgendaAtualizada, new { });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Os agendamentos do usuario {UsuarioId} foram concluidos, mas a notificacao em tempo real falhou.",
                        usuarioId);
                }

                return Ok(new { quantidade });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { erro = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { erro = "Não foi possível concluir os agendamentos do dia." });
            }
        }


        private async Task RegistrarNotificacoesClienteAsync(int agendamentoId)
        {
            // Apenas agendamentos criados por CLIENTE chegam aqui.
            // A empresa recebe uma notificacao e o profissional selecionado outra.
            // O autonomo recebe apenas uma; evita duplicidade via indice unico.
            const string sql = @"
                INSERT INTO NOTIFICACOES_AGENDAMENTO
                    (AGENDAMENTO_ID, DESTINATARIO_USUARIO_ID, TITULO, MENSAGEM, DATA_AGENDAMENTO)
                SELECT A.ID_AGENDAMENTO, destinatario.ID_USUARIO, 'Novo agendamento',
                    CU.NOME_USUARIO || ' agendou ' || S.NOME_SERVICO ||
                    CASE WHEN U.TIPO_USUARIO = 'EMPRESA'
                         THEN ' com ' || PU.NOME_USUARIO ELSE '' END ||
                    ' para ' || TO_CHAR(A.DATA_AGENDAMENTO, 'DD/MM/YYYY') ||
                    ' às ' || TO_CHAR(A.DATA_AGENDAMENTO, 'HH24:MI'),
                    A.DATA_AGENDAMENTO
                FROM AGENDAMENTOS A
                JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
                JOIN USUARIOS U ON U.ID_USUARIO = S.USUARIO_FK
                JOIN USUARIOS PU ON PU.ID_USUARIO = A.PRESTADOR_FK
                JOIN CLIENTES C ON C.ID_CLIENTE = A.CLIENTE_FK
                JOIN USUARIOS CU ON CU.ID_USUARIO = C.USUARIO_FK
                CROSS JOIN LATERAL (
                    SELECT U.ID_USUARIO
                    UNION
                    SELECT A.PRESTADOR_FK WHERE U.TIPO_USUARIO = 'EMPRESA'
                ) destinatario
                WHERE A.ID_AGENDAMENTO = @id
                ON CONFLICT (AGENDAMENTO_ID, DESTINATARIO_USUARIO_ID) DO NOTHING";
            await using var con = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await con.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", agendamentoId);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task NotificarAgendaAtualizada(
            AgendamentoModel agendamento,
            int responsavelId)
        {
            var usuariosNotificados = new HashSet<int> { responsavelId };

            if (agendamento.ClienteId > 0)
                usuariosNotificados.Add(agendamento.ClienteId);

            if (agendamento.ProfissionalId.GetValueOrDefault() > 0)
                usuariosNotificados.Add(agendamento.ProfissionalId!.Value);

            var payload = new
            {
                agendamento.Id,
                Data = agendamento.DataAgendamento.ToString("yyyy-MM-dd")
            };

            foreach (var usuarioId in usuariosNotificados)
            {
                await _agendaHub.Clients
                    .Group(AgendaHub.GrupoUsuario(usuarioId))
                    .SendAsync(AgendaHub.EventoAgendaAtualizada, payload);
            }
        }

        private async Task<bool> ProfissionalPodeAcessarAgenda(int usuarioId)
        {
            return !User.IsInRole(nameof(TipoUsuario.PROFISSIONAL)) ||
                   await _vinculoRepository.PossuiVinculoAprovadoAsync(usuarioId);
        }
    }
}
