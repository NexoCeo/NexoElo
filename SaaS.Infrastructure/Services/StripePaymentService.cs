using Microsoft.Extensions.Configuration;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using Stripe;

namespace SaaS.Infrastructure.Services
{
    public class StripePaymentService : IStripePaymentService
    {
        private readonly IAssinaturaRepository _assinaturaRepository;
        private readonly IConfiguration _configuration;
        private readonly StripeClient _stripeClient;

        public StripePaymentService(IAssinaturaRepository assinaturaRepository, IConfiguration configuration)
        {
            _assinaturaRepository = assinaturaRepository;
            _configuration = configuration;

            var secretKey = _configuration["Stripe:SecretKey"];
            if (string.IsNullOrWhiteSpace(secretKey))
                throw new InvalidOperationException("Configure Stripe:SecretKey para usar pagamentos via Stripe.");

            _stripeClient = new StripeClient(secretKey);
        }

        public async Task<StripeCheckoutResponseModel> CriarCheckoutAssinatura(CriarCheckoutAssinaturaModel model)
        {
            if (model.UsuarioId <= 0)
                throw new ArgumentException("UsuarioId inválido.");

            var priceId = ObterConfiguracao("Stripe:DefaultPriceId", "PriceId da assinatura não configurado.");
            var successUrl = ObterConfiguracao("Stripe:SuccessUrl", "SuccessUrl da Stripe não configurada.");
            var cancelUrl = ObterConfiguracao("Stripe:CancelUrl", "CancelUrl da Stripe não configurada.");

            var usuario = await _assinaturaRepository.ObterAssinaturaUsuario(model.UsuarioId);
            if (usuario == null)
                throw new KeyNotFoundException("Usuário não encontrado.");

            var customerId = usuario.StripeCustomerId;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                customerId = await CriarClienteStripe(usuario);
                await _assinaturaRepository.AtualizarClienteStripe(usuario.UsuarioId, customerId);
            }

            var metadata = CriarMetadata(usuario.UsuarioId, priceId);
            var options = new Stripe.Checkout.SessionCreateOptions
            {
                Mode = "subscription",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Customer = customerId,
                ClientReferenceId = usuario.UsuarioId.ToString(),
                AllowPromotionCodes = true,
                Metadata = metadata,
                SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
                {
                    Metadata = metadata
                },
                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                {
                    new()
                    {
                        Price = priceId,
                        Quantity = 1
                    }
                }
            };

            var service = new Stripe.Checkout.SessionService(_stripeClient);
            var session = await service.CreateAsync(options);

            await _assinaturaRepository.AtualizarAssinaturaStripe(
                usuario.UsuarioId,
                StatusAssinatura.PENDENTE,
                customerId,
                session.SubscriptionId,
                priceId,
                null);

            return new StripeCheckoutResponseModel
            {
                SessionId = session.Id,
                Url = session.Url ?? string.Empty,
                Status = StatusAssinatura.PENDENTE
            };
        }

        public async Task<StripePortalResponseModel> CriarPortalAssinatura(CriarPortalAssinaturaModel model)
        {
            if (model.UsuarioId <= 0)
                throw new ArgumentException("UsuarioId inválido.");

            var returnUrl = ObterConfiguracao("Stripe:PortalReturnUrl", "ReturnUrl do portal da Stripe não configurada.");
            var usuario = await _assinaturaRepository.ObterAssinaturaUsuario(model.UsuarioId);
            if (usuario == null)
                throw new KeyNotFoundException("Usuário não encontrado.");

            var customerId = usuario.StripeCustomerId;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                customerId = await CriarClienteStripe(usuario);
                await _assinaturaRepository.AtualizarClienteStripe(usuario.UsuarioId, customerId);
            }

            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = returnUrl
            };

            var service = new Stripe.BillingPortal.SessionService(_stripeClient);
            var session = await service.CreateAsync(options);

            return new StripePortalResponseModel
            {
                Url = session.Url ?? string.Empty
            };
        }

        public async Task<StripeCancelamentoResponseModel> CancelarAssinatura(int usuarioId, CancelarAssinaturaModel model)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuário inválido.");

            var usuario = await _assinaturaRepository.ObterAssinaturaUsuario(usuarioId);
            if (usuario == null)
                throw new KeyNotFoundException("Usuário não encontrado.");

            if (string.IsNullOrWhiteSpace(usuario.StripeSubscriptionId))
                throw new InvalidOperationException("Usuário não possui assinatura Stripe vinculada.");

            var service = new SubscriptionService(_stripeClient);
            Subscription subscription;

            if (model.CancelarAoFimDoPeriodo)
            {
                subscription = await service.UpdateAsync(usuario.StripeSubscriptionId, new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = true
                });
            }
            else
            {
                subscription = await service.CancelAsync(usuario.StripeSubscriptionId, new SubscriptionCancelOptions
                {
                    InvoiceNow = model.GerarFaturaFinal,
                    Prorate = model.AplicarProporcional
                });
            }

            await SincronizarAssinatura(subscription, usuarioId);
            return CriarCancelamentoResponse(subscription);
        }

        public async Task ProcessarWebhook(string json, string stripeSignature)
        {
            var endpointSecret = _configuration["Stripe:WebhookSecret"];
            if (string.IsNullOrWhiteSpace(endpointSecret))
                throw new InvalidOperationException("Configure Stripe:WebhookSecret para validar webhooks da Stripe.");

            var stripeEvent = EventUtility.ConstructEvent(json, stripeSignature, endpointSecret);

            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    await ProcessarCheckoutConcluido(stripeEvent);
                    break;
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                    await ProcessarAssinaturaAtualizada(stripeEvent);
                    break;
            }
        }

        private async Task<string> CriarClienteStripe(UsuarioAssinaturaInfoModel usuario)
        {
            var customerService = new CustomerService(_stripeClient);
            var customer = await customerService.CreateAsync(new CustomerCreateOptions
            {
                Name = usuario.Nome,
                Email = usuario.Email,
                Metadata = new Dictionary<string, string>
                {
                    ["usuario_id"] = usuario.UsuarioId.ToString()
                }
            });

            return customer.Id;
        }

        private async Task ProcessarCheckoutConcluido(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Stripe.Checkout.Session session || session.Mode != "subscription")
                return;

            var usuarioId = ObterUsuarioId(session.Metadata, session.ClientReferenceId);
            if (!usuarioId.HasValue || string.IsNullOrWhiteSpace(session.SubscriptionId))
                return;

            var subscriptionService = new SubscriptionService(_stripeClient);
            var subscription = await subscriptionService.GetAsync(session.SubscriptionId);

            await SincronizarAssinatura(subscription, usuarioId.Value);
        }

        private async Task ProcessarAssinaturaAtualizada(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Subscription subscription)
                return;

            var usuarioId = ObterUsuarioId(subscription.Metadata, null);
            if (usuarioId.HasValue)
            {
                await SincronizarAssinatura(subscription, usuarioId.Value);
                return;
            }

            await _assinaturaRepository.AtualizarAssinaturaPorStripeSubscriptionId(
                subscription.Id,
                MapearStatus(subscription),
                subscription.CustomerId,
                ObterPriceId(subscription),
                ObterDataFim(subscription));
        }

        private async Task SincronizarAssinatura(Subscription subscription, int usuarioId)
        {
            await _assinaturaRepository.AtualizarAssinaturaStripe(
                usuarioId,
                MapearStatus(subscription),
                subscription.CustomerId,
                subscription.Id,
                ObterPriceId(subscription),
                ObterDataFim(subscription));
        }

        private static StatusAssinatura MapearStatus(Subscription subscription)
        {
            if (subscription.Status == "active" && subscription.CancelAtPeriodEnd)
                return StatusAssinatura.CANCELAMENTO_PENDENTE;

            return subscription.Status switch
            {
                "active" or "trialing" => StatusAssinatura.ATIVA,
                "canceled" => StatusAssinatura.CANCELADA,
                "incomplete_expired" => StatusAssinatura.EXPIRADO,
                "past_due" or "unpaid" or "incomplete" or "paused" => StatusAssinatura.PENDENTE,
                _ => StatusAssinatura.PENDENTE
            };
        }

        private static DateTime? ObterDataFim(Subscription subscription)
        {
            return subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd
                ?? subscription.EndedAt
                ?? subscription.CanceledAt;
        }

        private static string? ObterPriceId(Subscription subscription)
        {
            return subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
        }

        private static StripeCancelamentoResponseModel CriarCancelamentoResponse(Subscription subscription)
        {
            return new StripeCancelamentoResponseModel
            {
                StripeSubscriptionId = subscription.Id,
                StripeStatus = subscription.Status,
                Status = MapearStatus(subscription),
                CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                DataFimAssinatura = ObterDataFim(subscription)
            };
        }

        private static int? ObterUsuarioId(IDictionary<string, string>? metadata, string? clientReferenceId)
        {
            if (metadata != null &&
                metadata.TryGetValue("usuario_id", out var usuarioIdMetadata) &&
                int.TryParse(usuarioIdMetadata, out var usuarioId))
            {
                return usuarioId;
            }

            return int.TryParse(clientReferenceId, out var referenceId) ? referenceId : null;
        }

        private static Dictionary<string, string> CriarMetadata(int usuarioId, string priceId)
        {
            return new Dictionary<string, string>
            {
                ["usuario_id"] = usuarioId.ToString(),
                ["price_id"] = priceId
            };
        }

        private string ObterConfiguracao(string chaveConfiguracao, string mensagemErro)
        {
            var valor = _configuration[chaveConfiguracao];

            if (string.IsNullOrWhiteSpace(valor))
                throw new InvalidOperationException(mensagemErro);

            return valor;
        }
    }
}


