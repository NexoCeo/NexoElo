using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AssinaturaController : ControllerBase
    {
        private readonly IAssinaturaRepository _assinaturaRepository;
        private readonly IStripePaymentService _stripePaymentService;

        public AssinaturaController(
            IAssinaturaRepository assinaturaRepository,
            IStripePaymentService stripePaymentService)
        {
            _assinaturaRepository = assinaturaRepository;
            _stripePaymentService = stripePaymentService;
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

            return Ok(new
            {
                Status = assinatura.Status,
                assinatura.DataFimAssinatura
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
            catch (Exception)
            {
                return BadRequest(new { Erro = "Nao foi possivel iniciar o checkout." });
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
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var signature = Request.Headers["Stripe-Signature"].ToString();

            try
            {
                await _stripePaymentService.ProcessarWebhook(json, signature);
                return Ok();
            }
            catch (Exception)
            {
                return BadRequest(new { Erro = "Webhook invalido." });
            }
        }
    }
}
