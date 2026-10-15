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
        private StripeClient? _stripeClient;
        private StripeClient Client => _stripeClient ??= new StripeClient(
            ObterConfiguracao("Stripe:SecretKey", "Configure Stripe:SecretKey para usar pagamentos via Stripe."));

        public StripePaymentService(IAssinaturaRepository assinaturaRepository, IConfiguration configuration)
        {
            _assinaturaRepository = assinaturaRepository;
            _configuration = configuration;
        }

        public async Task<StripeCheckoutResponseModel> CriarCheckoutAssinatura(CriarCheckoutAssinaturaModel model)
        {
            if (model.UsuarioId <= 0)
                throw new ArgumentException("UsuarioId inválido.");

            var plano = model.Plano?.Trim().ToUpperInvariant();
            if (plano is not ("BASICO" or "INTERMEDIARIO" or "AVANCADO"))
                throw new ArgumentException("Selecione um plano valido: BASICO, INTERMEDIARIO ou AVANCADO.");
            var priceId = ObterConfiguracao($"Stripe:Prices:{plano}", "Preco Stripe do plano nao configurado.");
            var successUrl = ObterConfiguracao("Stripe:SuccessUrl", "SuccessUrl da Stripe não configurada.");
            var cancelUrl = ObterConfiguracao("Stripe:CancelUrl", "CancelUrl da Stripe não configurada.");

            var usuario = await _assinaturaRepository.ObterAssinaturaUsuario(model.UsuarioId);
            if (usuario == null)
                throw new KeyNotFoundException("Usuário não encontrado.");

            if (usuario.Status is StatusAssinatura.ATIVA or StatusAssinatura.CANCELAMENTO_PENDENTE)
                throw new InvalidOperationException("Utilize o portal para gerenciar uma assinatura existente.");

            if (!await _assinaturaRepository.ReservarCheckoutAsync(usuario.UsuarioId))
                throw new InvalidOperationException("Ja existe um checkout em andamento. Aguarde ou retome a contratacao anterior.");

            try
            {
                return await CriarCheckoutReservadoAsync(usuario, priceId, successUrl, cancelUrl);
            }
            catch
            {
                await _assinaturaRepository.LiberarCheckoutAsync(usuario.UsuarioId);
                throw;
            }
        }

        private async Task<StripeCheckoutResponseModel> CriarCheckoutReservadoAsync(
            UsuarioAssinaturaInfoModel usuario, string priceId, string successUrl, string cancelUrl)
        {
            if (usuario.StripeCustomerId is not null)
            {
                var assinaturas = await new SubscriptionService(Client).ListAsync(new SubscriptionListOptions
                {
                    Customer = usuario.StripeCustomerId,
                    Status = "all",
                    Limit = 100
                });
                if (assinaturas.Data.Any(s => s.Status is "active" or "trialing" or "past_due" or "unpaid" or "incomplete"))
                    throw new InvalidOperationException("Ja existe uma assinatura Stripe em andamento para esta conta.");
                
                }
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
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
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

            var service = new Stripe.Checkout.SessionService(Client);
            var session = await service.CreateAsync(options);

            await _assinaturaRepository.RegistrarSessaoCheckoutAsync(usuario.UsuarioId, session.Id);
            // Checkout nao libera nem suspende o periodo gratuito.
            // O webhook verificado confirma a assinatura antes de alterar o acesso.

            return new StripeCheckoutResponseModel
            {
                SessionId = session.Id,
                Url = session.Url ?? string.Empty,
                Status = StatusAssinatura.PENDENTE
            };
        }

        public async Task AlterarPlanoAssinatura(int usuarioId, string plano, bool reativarRenovacao = false)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuario invalido.");

            var planoNormalizado = plano?.Trim().ToUpperInvariant();
            if (planoNormalizado is not ("BASICO" or "INTERMEDIARIO" or "AVANCADO"))
                throw new ArgumentException("Plano invalido.");

            var precoDestino = ObterConfiguracao($"Stripe:Prices:{planoNormalizado}",
                "Preco Stripe do plano nao configurado.");
            var usuario = await _assinaturaRepository.ObterAssinaturaUsuario(usuarioId)
                ?? throw new KeyNotFoundException("Usuario nao encontrado.");

            if (usuario.Status is not (StatusAssinatura.ATIVA or StatusAssinatura.CANCELAMENTO_PENDENTE) ||
                string.IsNullOrWhiteSpace(usuario.StripeSubscriptionId))
                throw new InvalidOperationException("Nao existe assinatura ativa para alterar.");

            var servico = new SubscriptionService(Client);
            var atual = await servico.GetAsync(usuario.StripeSubscriptionId);
            if (atual.Status != "active")
                throw new InvalidOperationException("A assinatura precisa estar ativa e sem cancelamento agendado.");

            if (atual.CancelAtPeriodEnd && !reativarRenovacao)
                throw new InvalidOperationException("Confirme a reativacao da renovacao antes da mudanca.");

            if (!string.Equals(atual.CustomerId, usuario.StripeCustomerId, StringComparison.Ordinal))
                throw new InvalidOperationException("A assinatura nao pertence ao cliente esperado.");

            var item = atual.Items?.Data?.SingleOrDefault();
            if (item == null)
                throw new InvalidOperationException("A assinatura nao possui exatamente um item.");

            if (item.Price?.Id == precoDestino)
                throw new InvalidOperationException("Este ja e o plano atual.");

            // Sem cobranca proporcional imediata. O novo valor passa a valer na
            // proxima renovacao, enquanto os recursos mudam apos confirmacao Stripe.
            var alterada = await servico.UpdateAsync(atual.Id, new SubscriptionUpdateOptions
            {
                ProrationBehavior = "none",
                CancelAtPeriodEnd = reativarRenovacao ? false : null,
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Id = item.Id, Price = precoDestino, Quantity = 1 }
                }
            });

            await SincronizarAssinatura(alterada, usuarioId);
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

            var service = new Stripe.BillingPortal.SessionService(Client);
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

            var service = new SubscriptionService(Client);
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
            if (string.IsNullOrWhiteSpace(stripeEvent.Id))
                throw new InvalidOperationException("Evento sem identificador.");

            if (await _assinaturaRepository.WebhookJaProcessadoAsync(stripeEvent.Id))
                return;

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
            await _assinaturaRepository.MarcarWebhookProcessadoAsync(stripeEvent.Id);
        }

        private async Task<string> CriarClienteStripe(UsuarioAssinaturaInfoModel usuario)
        {
            var customerService = new CustomerService(Client);
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

            var subscriptionService = new SubscriptionService(Client);
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


