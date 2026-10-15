using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;
using SaaS.Api.Services;
using Stripe;
using Npgsql;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AssinaturaController : ControllerBase
    {
        private readonly IAssinaturaRepository _assinaturaRepository;
        private readonly IStripePaymentService _stripePaymentService;
        private readonly PlanoAcessoService _planos;
        private readonly ILogger<AssinaturaController> _logger;

        public AssinaturaController(
            IAssinaturaRepository assinaturaRepository,
            IStripePaymentService stripePaymentService,
            PlanoAcessoService planos,
            ILogger<AssinaturaController> logger)
        {
            _assinaturaRepository = assinaturaRepository;
            _stripePaymentService = stripePaymentService;
            _planos = planos;
            _logger = logger;
        }

        [HttpGet("{id}")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> ObterStatus(int id)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            var assinatura = await _assinaturaRepository.ObterAssinaturaUsuario(id);
            if (assinatura == null)
                return NotFound("Usuario nao encontrado");

            var acesso = await _planos.ConsultarAsync(id);
            Response.Headers.CacheControl = "no-store";
            return Ok(acesso);
        }

        [HttpGet("minha")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> MinhaAssinatura()
        {
            if (!User.TryGetUsuarioId(out var id)) return Unauthorized();
            var acesso = await _planos.ConsultarAsync(id);
            var servicos = await _planos.ContarServicosAsync(id);
            var profissionais = User.IsInRole("EMPRESA")
                ? await _planos.ContarProfissionaisAsync(id)
                : 0;
            Response.Headers.CacheControl = "no-store";
            return Ok(new
            {
                acesso.Plano,
                acesso.AcessoAtivo,
                acesso.RelatoriosLiberados,
                acesso.LimiteServicos,
                acesso.LimiteProfissionais,
                acesso.DataFim,
                acesso.Status,
                ServicosCadastrados = servicos,
                ProfissionaisVinculados = profissionais
            });
        }

        [HttpPost("checkout")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> CriarCheckout([FromBody] CriarCheckoutAssinaturaModel model)
        {
            if (!User.EhProprioUsuario(model.UsuarioId))
                return Forbid();

            try
            {
                var session = await _stripePaymentService.CriarCheckoutAssinatura(model);
                return Ok(session);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Erro = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Configuracao ou estado invalido ao criar checkout de assinatura");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Erro = "O pagamento nao esta disponivel no momento. Verifique a configuracao de precos, URLs e credenciais Stripe no servidor.",
                    Codigo = "STRIPE_CONFIGURACAO"
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe recusou a criacao do checkout");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Erro = "Nao foi possivel criar a sessao de pagamento com a Stripe. Tente novamente ou fale com o suporte.",
                    Codigo = "STRIPE_INDISPONIVEL"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado ao iniciar checkout de assinatura");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Erro = "Erro interno ao iniciar o checkout." });
            }
        }

        [HttpGet("recursos")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> ListarRecursos()
        {
            if (!User.TryGetUsuarioId(out var id)) return Unauthorized();
            return Ok(await _planos.ListarRecursosAsync(id,User.IsInRole("EMPRESA")));
        }

        [HttpPut("recursos")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> AtualizarRecursosAtivos([FromBody] CriarCheckoutAssinaturaModel model)
        {
            if (!User.EhProprioUsuario(model.UsuarioId)) return Forbid();
            var atual = await _planos.ConsultarAsync(model.UsuarioId);
            if (!atual.AcessoAtivo || atual.Plano is not ("BASICO" or "INTERMEDIARIO" or "AVANCADO"))
                return Conflict(new { Erro = "Assinatura ativa obrigatoria para regularizar recursos." });
            try
            {
                await _planos.ValidarSelecaoAsync(model.UsuarioId, User.IsInRole("EMPRESA"), atual.Plano,
                    model.ServicosAtivos, model.ProfissionaisAtivos);
                await _planos.SalvarSelecaoAsync(model.UsuarioId, User.IsInRole("EMPRESA"), atual.Plano,
                    model.ServicosAtivos, model.ProfissionaisAtivos);
                return NoContent();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Erro = ex.Message });
            }
        }

        [HttpPost("alterar-plano")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> AlterarPlano([FromBody] CriarCheckoutAssinaturaModel model)
        {
            if (!User.EhProprioUsuario(model.UsuarioId))
                return Forbid();

            try
            {
                var plano = model.Plano?.Trim().ToUpperInvariant() ?? "";
                if (plano is not ("BASICO" or "INTERMEDIARIO" or "AVANCADO"))
                    return BadRequest(new { Erro = "Plano invalido." });
                await _planos.ValidarSelecaoAsync(model.UsuarioId, User.IsInRole("EMPRESA"), plano,
                    model.ServicosAtivos, model.ProfissionaisAtivos);
                await _stripePaymentService.AlterarPlanoAssinatura(model.UsuarioId, plano);
                await _planos.SalvarSelecaoAsync(model.UsuarioId, User.IsInRole("EMPRESA"), plano,
                    model.ServicosAtivos, model.ProfissionaisAtivos);
                return Ok(new { Mensagem = "Plano atualizado. O novo valor sera aplicado na proxima renovacao." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Erro = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { Erro = "Assinatura nao encontrada." });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Alteracao de plano nao permitida. UsuarioId={UsuarioId}", model.UsuarioId);
                return Conflict(new { Erro = ex.Message });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Erro Stripe ao alterar plano. UsuarioId={UsuarioId}", model.UsuarioId);
                return StatusCode(502, new { Erro = "A Stripe nao conseguiu atualizar o plano." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro interno ao alterar plano. UsuarioId={UsuarioId}", model.UsuarioId);
                return StatusCode(500, new { Erro = "Nao foi possivel atualizar o plano." });
            }
        }

        [HttpPost("portal")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> CriarPortal([FromBody] CriarPortalAssinaturaModel model)
        {
            if (!User.EhProprioUsuario(model.UsuarioId))
                return Forbid();

            try
            {
                var portal = await _stripePaymentService.CriarPortalAssinatura(model);
                return Ok(portal);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception)
            {
                return BadRequest(new { Erro = "Nao foi possivel abrir o portal da assinatura." });
            }
        }

        [HttpPost("{id}/cancelar")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> CancelarAssinatura(int id, [FromBody] CancelarAssinaturaModel? model)
        {
            if (!User.EhProprioUsuario(id))
                return Forbid();

            try
            {
                var assinatura = await _stripePaymentService.CancelarAssinatura(id, model ?? new CancelarAssinaturaModel());
                return Ok(new
                {
                    assinatura.StripeSubscriptionId,
                    assinatura.StripeStatus,
                    assinatura.Status,
                    assinatura.CancelAtPeriodEnd,
                    assinatura.DataFimAssinatura
                });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception)
            {
                return BadRequest(new { Erro = "Nao foi possivel cancelar a assinatura." });
            }
        }

        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> StripeWebhook()
        {
            // O corpo bruto e a assinatura sao repassados sem modificacoes.
            // Nunca registrar payload, Stripe-Signature nem segredos em logs.
            var signature = Request.Headers["Stripe-Signature"].ToString();
            var traceId = HttpContext.TraceIdentifier;
            if (string.IsNullOrWhiteSpace(signature))
            {
                _logger.LogWarning("Stripe webhook sem cabecalho Stripe-Signature. TraceId={TraceId}", traceId);
                return BadRequest(new { Erro = "Assinatura do webhook ausente.", Codigo = "STRIPE_SIGNATURE_MISSING" });
            }

            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            try
            {
                await _stripePaymentService.ProcessarWebhook(json, signature);
                _logger.LogInformation("Stripe webhook processado. TraceId={TraceId}", traceId);
                return Ok();
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Falha de validacao ou processamento Stripe no webhook. Tipo={ExceptionType}, TraceId={TraceId}",
                    ex.GetType().Name, traceId);
                return BadRequest(new { Erro = "Evento Stripe rejeitado.", Codigo = "STRIPE_EVENT_INVALID", TraceId = traceId });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Configuracao ou formato de evento Stripe invalido. Tipo={ExceptionType}, TraceId={TraceId}",
                    ex.GetType().Name, traceId);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { Erro = "Nao foi possivel processar o evento.", Codigo = "STRIPE_EVENT_PROCESSING", TraceId = traceId });
            }
            catch (PostgresException ex)
            {
                _logger.LogError(ex, "Falha PostgreSQL ao sincronizar assinatura Stripe. SqlState={SqlState}, Constraint={Constraint}, TraceId={TraceId}",
                    ex.SqlState, ex.ConstraintName, traceId);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { Erro = "Falha ao sincronizar a assinatura.", Codigo = "SUBSCRIPTION_DATABASE_ERROR", TraceId = traceId });
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "Falha de conexao ou operacao PostgreSQL no webhook. TraceId={TraceId}", traceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { Erro = "Banco de dados temporariamente indisponivel.", Codigo = "SUBSCRIPTION_DATABASE_UNAVAILABLE", TraceId = traceId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao processar webhook Stripe. Tipo={ExceptionType}, TraceId={TraceId}",
                    ex.GetType().Name, traceId);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { Erro = "Falha interna ao processar o evento.", Codigo = "STRIPE_WEBHOOK_INTERNAL_ERROR", TraceId = traceId });
            }
        }
    }
}
