using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SaaS.Api.Security;
using Stripe;
using Stripe.Checkout;

namespace SaaS.Api.Controllers;

/// <summary>Pagamentos de atendimentos. Não reutiliza nem altera o faturamento das assinaturas.</summary>
[ApiController]
[Route("api/pagamentos-agendamento")]
public sealed class PagamentosAgendamentoController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<PagamentosAgendamentoController> _logger;
    private readonly string _connectionString;
    private StripeClient StripeClient => new(Required("Stripe:SecretKey"));

    public PagamentosAgendamentoController(IConfiguration config, ILogger<PagamentosAgendamentoController> logger)
    {
        _config = config;
        _logger = logger;
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Banco de dados não configurado.");
    }

    public sealed record ModoPagamentoRequest(string Modalidade);
    public sealed record ConfiguracaoPagamentoResponse(string Modalidade, bool OnlineDisponivel,
        bool ContaConectada, string? ContaStripe = null,
        int TarifaCartaoBps = 399, long TarifaCartaoFixaCentavos = 39);
    private sealed record Cobranca(int AgendamentoId, int RecebedorId, int ClienteId, decimal Valor,
        string StatusAgendamento, string Modalidade, string? ContaStripe);

    private string FrontendBaseUrl()
    {
        // Reutiliza somente a origem pública do frontend já autorizada no CORS.
        // Nunca utilize diretamente uma origem fornecida pelo cliente (Origin/Referer),
        // pois as URLs de retorno são transmitidas à Stripe.
        var origins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var found = origins
            .Select(value => value?.Trim().TrimEnd('/'))
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("nexoelo.nexoceo.com.br", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (found.Length != 1)
            throw new InvalidOperationException(
                "Configure https://nexoelo.nexoceo.com.br em Cors:AllowedOrigins para o retorno dos pagamentos.");

        return found[0]!;
    }

    private string Required(string name) => !string.IsNullOrWhiteSpace(_config[name])
        ? _config[name]! : throw new InvalidOperationException("Configuração ausente: " + name);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<bool> RecebedorAutorizado(NpgsqlConnection conn, int id)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM USUARIOS WHERE ID_USUARIO=@id AND TIPO_USUARIO IN ('EMPRESA','AUTONOMO'))",conn);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    private async Task<(string Modalidade, string? Conta)> LerConfig(NpgsqlConnection conn, int recebedorId)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT MODALIDADE, STRIPE_CONNECTED_ACCOUNT_ID
              FROM CONFIGURACOES_PAGAMENTO WHERE USUARIO_FK=@id",conn);
        cmd.Parameters.AddWithValue("id", recebedorId);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return ("PRESENCIAL", null);
        return (rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1));
    }

    private async Task<bool> ContaApta(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        try
        {
            var account = await new AccountService(StripeClient).GetAsync(accountId);
            return account.ChargesEnabled && account.PayoutsEnabled;
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Não foi possível verificar a conta Stripe Connect.");
            return false;
        }
    }

    [HttpGet("configuracao/{recebedorId:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfiguracaoPublica(int recebedorId)
    {
        await using var conn = await OpenAsync();
        if (!await RecebedorAutorizado(conn, recebedorId)) return NotFound();
        var (modalidade, conta) = await LerConfig(conn, recebedorId);
        var online = (modalidade is "ONLINE" or "AMBOS") && await ContaApta(conta);
        return Ok(new ConfiguracaoPagamentoResponse(modalidade, online, online, null, TarifaPercentualBps(), TarifaFixaCentavos()));
    }

    [HttpGet("minha-configuracao")]
    [Authorize(Roles = "EMPRESA,AUTONOMO")]
    public async Task<IActionResult> MinhaConfiguracao()
    {
        if (!User.TryGetUsuarioId(out var id)) return Unauthorized();
        await using var conn = await OpenAsync();
        var (modalidade, conta) = await LerConfig(conn,id);
        var apta = await ContaApta(conta);
        return Ok(new ConfiguracaoPagamentoResponse(modalidade, apta && (modalidade is "ONLINE" or "AMBOS"),
            apta, conta, TarifaPercentualBps(), TarifaFixaCentavos()));
    }

    [HttpPut("minha-configuracao")]
    [Authorize(Roles = "EMPRESA,AUTONOMO")]
    public async Task<IActionResult> SalvarConfiguracao([FromBody] ModoPagamentoRequest request)
    {
        if (!User.TryGetUsuarioId(out var id)) return Unauthorized();
        var modo = request.Modalidade?.Trim().ToUpperInvariant();
        if (modo is not ("PRESENCIAL" or "ONLINE" or "AMBOS"))
            return BadRequest(new { erro = "Modalidade inválida." });
        await using var conn = await OpenAsync();
        if (modo != "PRESENCIAL")
        {
            var (_, conta) = await LerConfig(conn, id);
            if (!await ContaApta(conta))
                return Conflict(new { erro = "Conecte e habilite sua conta Stripe antes de ativar pagamentos online." });
        }
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO CONFIGURACOES_PAGAMENTO(USUARIO_FK,MODALIDADE)
            VALUES(@id,@modo)
            ON CONFLICT(USUARIO_FK) DO UPDATE SET MODALIDADE=EXCLUDED.MODALIDADE,
                ATUALIZADO_EM=NOW()", conn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("modo",modo!);
        await cmd.ExecuteNonQueryAsync();
        return NoContent();
    }

    [HttpPost("conectar-stripe")]
    [Authorize(Roles = "EMPRESA,AUTONOMO")]
    public async Task<IActionResult> ConectarStripe()
    {
        if (!User.TryGetUsuarioId(out var id)) return Unauthorized();
        // Validar a URL antes de criar qualquer recurso na Stripe.
        var frontend = FrontendBaseUrl();
        await using var conn = await OpenAsync();
        var (_, accountId) = await LerConfig(conn,id);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            // Conta Express própria do estabelecimento. Não reutilizar o Customer das assinaturas.
            Stripe.Account created;
            try
            {
                created = await new AccountService(StripeClient).CreateAsync(new AccountCreateOptions
                {
                    Type="express", Country="BR",
                    Metadata = new Dictionary<string,string> { ["nexoelo_recebedor_id"]=id.ToString() }
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex,
                    "Stripe Connect recusou a criação de conta para recebedor {RecebedorId}; código {StripeCode}",
                    id, ex.StripeError?.Code);
                var message = ex.Message.Contains("Accounts v1", StringComparison.OrdinalIgnoreCase)
                    ? "Sua conta Stripe ainda não permite a criação de contas Connect pela API Accounts v1. Habilite compatibilidade com Accounts v1 nas configurações da Stripe ou migre esta integração para Accounts v2."
                    : ex.Message.Contains("Connect", StringComparison.OrdinalIgnoreCase)
                    ? "A Stripe não autorizou a criação da conta Connect. Verifique se o Connect está habilitado para sua plataforma e ambiente de testes."
                    : "A Stripe recusou a criação da conta recebedora. Consulte os logs da API e os registros de requisições no painel Stripe.";
                return StatusCode(StatusCodes.Status422UnprocessableEntity,
                    new { erro = message, codigo = ex.StripeError?.Code ?? "stripe_connect_error" });
            }
            accountId = created.Id;
            await using var update = new NpgsqlCommand(@"
                INSERT INTO CONFIGURACOES_PAGAMENTO(USUARIO_FK,STRIPE_CONNECTED_ACCOUNT_ID)
                VALUES(@id,@conta)
                ON CONFLICT(USUARIO_FK) DO UPDATE SET
                 STRIPE_CONNECTED_ACCOUNT_ID = COALESCE(CONFIGURACOES_PAGAMENTO.STRIPE_CONNECTED_ACCOUNT_ID,
                                                         EXCLUDED.STRIPE_CONNECTED_ACCOUNT_ID),
                 ATUALIZADO_EM = NOW()", conn);
            update.Parameters.AddWithValue("id",id);
            update.Parameters.AddWithValue("conta",accountId);
            await update.ExecuteNonQueryAsync();
            // Se houve conexão simultânea, considerar sempre a conta já persistida.
            (_,accountId) = await LerConfig(conn,id);
        }
        var basePath = User.IsInRole("EMPRESA") ? "/empresa/pagamentos" : "/autonomo/pagamentos";
        try
        {
            var link = await new AccountLinkService(StripeClient).CreateAsync(new AccountLinkCreateOptions
            {
                Account=accountId, Type="account_onboarding",
                RefreshUrl=frontend+basePath+"?stripe=renovar",
                ReturnUrl=frontend+basePath+"?stripe=retorno"
            });
            return Ok(new { url=link.Url });
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex,
                "Stripe Connect recusou link de onboarding para recebedor {RecebedorId}; código {StripeCode}",
                id, ex.StripeError?.Code);
            return StatusCode(StatusCodes.Status422UnprocessableEntity,
                new { erro = "Não foi possível gerar o link de cadastro da Stripe. Consulte os logs do backend para identificar a restrição da conta.",
                    codigo = ex.StripeError?.Code ?? "stripe_onboarding_error" });
        }
    }

    private async Task<Cobranca?> LerCobranca(NpgsqlConnection conn, int agendamentoId)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT A.ID_AGENDAMENTO, S.USUARIO_FK, C.USUARIO_FK,
                   COALESCE((SELECT SUM(SV.VALOR_SERVICO)
                     FROM AGENDAMENTO_SERVICOS APS
                     JOIN SERVICOS SV ON SV.ID_SERVICO=APS.SERVICO_FK
                     WHERE APS.AGENDAMENTO_FK=A.ID_AGENDAMENTO), S.VALOR_SERVICO),
                   A.STATUS_AGENDAMENTO,
                   COALESCE(CP.MODALIDADE,'PRESENCIAL'),CP.STRIPE_CONNECTED_ACCOUNT_ID
              FROM AGENDAMENTOS A
              JOIN SERVICOS S ON S.ID_SERVICO=A.SERVICO_FK
              JOIN CLIENTES C ON C.ID_CLIENTE=A.CLIENTE_FK
              LEFT JOIN CONFIGURACOES_PAGAMENTO CP ON CP.USUARIO_FK=S.USUARIO_FK
             WHERE A.ID_AGENDAMENTO=@id",conn);
        cmd.Parameters.AddWithValue("id",agendamentoId);
        await using var rd=await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new Cobranca(rd.GetInt32(0),rd.GetInt32(1),rd.GetInt32(2),rd.GetDecimal(3),
            rd.GetString(4),rd.GetString(5),rd.IsDBNull(6)?null:rd.GetString(6));
    }

    private static long Centavos(decimal valor) => checked((long)decimal.Round(valor*100,0,MidpointRounding.AwayFromZero));

    // Tarifa de processamento estimada para cartão nacional: 3,99% + R$ 0,39.
    // Permite atualizar a precificação sem alterar assinaturas ou valores de serviços.
    // É obrigatório conferir as tarifas reais contratadas no Stripe antes da produção.
    private int TarifaPercentualBps() => int.TryParse(_config["PagamentoAgendamento:TarifaCartaoBps"], out var bps)
        && bps >= 0 && bps < 10000 ? bps : 399;
    private long TarifaFixaCentavos() => long.TryParse(_config["PagamentoAgendamento:TarifaCartaoFixaCentavos"], out var valor)
        && valor >= 0 ? valor : 39;

    private (long Total, long Tarifa) CalcularCobrancaOnline(long valorServico)
    {
        if (valorServico <= 0) throw new ArgumentOutOfRangeException(nameof(valorServico));
        // Arredonda para cima: o valor transferido ao prestador nunca é reduzido.
        var total = checked((long)decimal.Ceiling(
            (valorServico + TarifaFixaCentavos()) * 10000m / (10000 - TarifaPercentualBps())));
        return (total, total - valorServico);
    }
    private static bool Aberto(Cobranca pedido) => pedido.StatusAgendamento.Trim().Equals("AGENDADO",StringComparison.OrdinalIgnoreCase);

    [HttpGet("agendamento/{id:int}")]
    [Authorize(Roles="CLIENTE,EMPRESA,AUTONOMO")]
    public async Task<IActionResult> ConsultarPagamento(int id)
    {
        if (!User.TryGetUsuarioId(out var userId)) return Unauthorized();
        await using var conn=await OpenAsync();
        var agendamento=await LerCobranca(conn,id);
        if (agendamento is null) return NotFound();
        if (agendamento.ClienteId!=userId && agendamento.RecebedorId!=userId) return Forbid();
        await using var cmd=new NpgsqlCommand(@"
           SELECT METODO,STATUS,VALOR_CENTAVOS FROM PAGAMENTOS_AGENDAMENTO WHERE AGENDAMENTO_FK=@id",conn);
        cmd.Parameters.AddWithValue("id",id);
        await using var rd=await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return Ok(new { status="NAO_INICIADO",valor=agendamento.Valor });
        return Ok(new { metodo=rd.GetString(0),status=rd.GetString(1),valor=rd.GetInt64(2)/100m });
    }

    [HttpPost("agendamento/{id:int}/presencial")]
    [Authorize(Roles="CLIENTE")]
    public async Task<IActionResult> RegistrarPresencial(int id)
    {
        if (!User.TryGetUsuarioId(out var userId)) return Unauthorized();
        await using var conn=await OpenAsync();
        var a=await LerCobranca(conn,id);
        if (a is null) return NotFound();
        if (a.ClienteId != userId) return Forbid();
        if (!Aberto(a)) return Conflict(new {erro="Agendamento não está ativo."});
        if (a.Modalidade is not ("PRESENCIAL" or "AMBOS"))
            return Conflict(new {erro="O estabelecimento não aceita pagamento presencial."});
        await using var cmd=new NpgsqlCommand(@"
          INSERT INTO PAGAMENTOS_AGENDAMENTO
           (AGENDAMENTO_FK,RECEBEDOR_USUARIO_FK,CLIENTE_USUARIO_FK,VALOR_CENTAVOS,METODO,STATUS)
          VALUES(@a,@r,@c,@v,'PRESENCIAL','PENDENTE')
          ON CONFLICT(AGENDAMENTO_FK) DO NOTHING",conn);
        cmd.Parameters.AddWithValue("a",id);
        cmd.Parameters.AddWithValue("r",a.RecebedorId);
        cmd.Parameters.AddWithValue("c",userId);
        cmd.Parameters.AddWithValue("v",Centavos(a.Valor));
        if(await cmd.ExecuteNonQueryAsync()==0)
            return Conflict(new {erro="Já existe uma modalidade de pagamento registrada para este agendamento."});
        return Ok(new {status="PENDENTE",metodo="PRESENCIAL"});
    }

    [HttpPost("agendamento/{id:int}/checkout")]
    [Authorize(Roles="CLIENTE")]
    public async Task<IActionResult> Checkout(int id)
    {
        if (!User.TryGetUsuarioId(out var userId)) return Unauthorized();
        await using var conn=await OpenAsync();
        var a=await LerCobranca(conn,id);
        if (a is null) return NotFound();
        if (a.ClienteId != userId) return Forbid();
        if (!Aberto(a)) return Conflict(new {erro="Agendamento não está ativo."});
        if (a.Modalidade is not ("ONLINE" or "AMBOS") || !await ContaApta(a.ContaStripe))
            return Conflict(new {erro="Pagamento online indisponível para este estabelecimento."});
        var valorServicoCentavos=Centavos(a.Valor);
        if(valorServicoCentavos<=0) return BadRequest(new {erro="Valor de cobrança inválido."});
        var (cents, tarifaCentavos)=CalcularCobrancaOnline(valorServicoCentavos);

        await using var transaction=await conn.BeginTransactionAsync();
        // Impede checkouts concorrentes do mesmo agendamento.
        await using(var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(8741,@id)",conn,transaction))
        {
            lockCmd.Parameters.AddWithValue("id",id);
            await lockCmd.ExecuteNonQueryAsync();
        }
        var tentativa = 0;
        await using(var existing = new NpgsqlCommand(@"
          SELECT STATUS,STRIPE_SESSION_ID,METODO,TENTATIVA
          FROM PAGAMENTOS_AGENDAMENTO WHERE AGENDAMENTO_FK=@id FOR UPDATE",conn,transaction))
        {
            existing.Parameters.AddWithValue("id",id);
            await using var rd=await existing.ExecuteReaderAsync();
            if(await rd.ReadAsync())
            {
                var status=rd.GetString(0);
                var session=rd.IsDBNull(1)?null:rd.GetString(1);
                var metodo=rd.GetString(2);
                tentativa=rd.GetInt32(3);
                await rd.CloseAsync();
                if(status=="PAGO" || metodo!="ONLINE") return Conflict(new {erro="Pagamento já registrado."});
                if(!string.IsNullOrWhiteSpace(session))
                {
                    var existingSession=await new SessionService(StripeClient).GetAsync(session);
                    if(existingSession.Status=="open" && existingSession.AmountTotal == cents
                        && !string.IsNullOrWhiteSpace(existingSession.Url))
                        return Ok(new {url=existingSession.Url,
                            valorServico=valorServicoCentavos/100m, tarifa=tarifaCentavos/100m, total=cents/100m});
                    // Sessões antigas sem a taxa visível não podem ser reutilizadas.
                    if(existingSession.Status=="open" && existingSession.AmountTotal != cents)
                    {
                        await new SessionService(StripeClient).ExpireAsync(session);
                        existingSession.Status = "expired";
                    }
                    if(existingSession.Status != "expired" || existingSession.PaymentStatus == "paid")
                        return Conflict(new {erro="Aguardando a confirmação do pagamento anterior."});
                    tentativa++;
                    // A sessão antiga expirou e não pode mais receber pagamento.
                    await using var reset = new NpgsqlCommand(@"
                        UPDATE PAGAMENTOS_AGENDAMENTO SET STRIPE_SESSION_ID=NULL,
                            TENTATIVA=@tentativa,ATUALIZADO_EM=NOW()
                        WHERE AGENDAMENTO_FK=@id AND METODO='ONLINE' AND STATUS='PENDENTE'",conn,transaction);
                    reset.Parameters.AddWithValue("tentativa",tentativa);
                    reset.Parameters.AddWithValue("id",id);
                    await reset.ExecuteNonQueryAsync();
                }
            }
            else await rd.CloseAsync();
        }

        var frontend=FrontendBaseUrl();
        var options=new SessionCreateOptions {
            Mode="payment",
            SuccessUrl=frontend+"/cliente-final/home?pagamento=retorno",
            CancelUrl=frontend+"/cliente-final/home?pagamento=cancelado",
            ClientReferenceId=id.ToString(),
            Metadata=new Dictionary<string,string> {["agendamento_id"]=id.ToString(),
                ["recebedor_id"]=a.RecebedorId.ToString()},
            PaymentIntentData=new SessionPaymentIntentDataOptions {
                TransferData=new SessionPaymentIntentDataTransferDataOptions {Destination=a.ContaStripe, Amount=valorServicoCentavos}
            },
            // O checkout informa separadamente valor do serviço e custo do pagamento online.
            // O valor a transferir permanece igual ao preço cadastrado pelo prestador.
            PaymentMethodTypes=new List<string> { "card" },
            LineItems=new List<SessionLineItemOptions> {
                new() { Quantity=1, PriceData=new SessionLineItemPriceDataOptions {
                    Currency="brl",UnitAmount=valorServicoCentavos,
                    ProductData=new SessionLineItemPriceDataProductDataOptions {
                        Name="Serviços do agendamento #"+id
                    }
                }},
                new() { Quantity=1, PriceData=new SessionLineItemPriceDataOptions {
                    Currency="brl",UnitAmount=tarifaCentavos,
                    ProductData=new SessionLineItemPriceDataProductDataOptions {
                        Name="Custo do pagamento online"
                    }
                }}
            }
        };
        var checkout=await new SessionService(StripeClient).CreateAsync(options,
            new RequestOptions {IdempotencyKey="nexoelo-agendamento-checkout-"+id+"-"+tentativa});
        await using(var insert=new NpgsqlCommand(@"
          INSERT INTO PAGAMENTOS_AGENDAMENTO
           (AGENDAMENTO_FK,RECEBEDOR_USUARIO_FK,CLIENTE_USUARIO_FK,VALOR_CENTAVOS,METODO,STATUS,STRIPE_SESSION_ID)
          VALUES(@a,@r,@c,@v,'ONLINE','PENDENTE',@session)
          ON CONFLICT(AGENDAMENTO_FK) DO UPDATE SET
           STRIPE_SESSION_ID=EXCLUDED.STRIPE_SESSION_ID,
           ATUALIZADO_EM=NOW()
          WHERE PAGAMENTOS_AGENDAMENTO.METODO='ONLINE'
            AND PAGAMENTOS_AGENDAMENTO.STATUS='PENDENTE'
            AND PAGAMENTOS_AGENDAMENTO.STRIPE_SESSION_ID IS NULL",conn,transaction))
        {
            insert.Parameters.AddWithValue("a",id);
            insert.Parameters.AddWithValue("r",a.RecebedorId);
            insert.Parameters.AddWithValue("c",userId);
            insert.Parameters.AddWithValue("v",cents);
            insert.Parameters.AddWithValue("session",checkout.Id);
            if(await insert.ExecuteNonQueryAsync()!=1)
                return Conflict(new {erro="Outro pagamento já foi registrado."});
        }
        await transaction.CommitAsync();
        return Ok(new {url=checkout.Url, valorServico=valorServicoCentavos/100m, tarifa=tarifaCentavos/100m, total=cents/100m});
    }

    [HttpGet("pendentes")]
    [Authorize(Roles="EMPRESA,AUTONOMO")]
    public async Task<IActionResult> ListarPendentes()
    {
        if(!User.TryGetUsuarioId(out var recebedor)) return Unauthorized();
        await using var conn=await OpenAsync();
        await using var cmd=new NpgsqlCommand(@"
          SELECT P.AGENDAMENTO_FK, P.VALOR_CENTAVOS, A.DATA_AGENDAMENTO,
                 U.NOME_USUARIO AS CLIENTE
          FROM PAGAMENTOS_AGENDAMENTO P
          JOIN AGENDAMENTOS A ON A.ID_AGENDAMENTO=P.AGENDAMENTO_FK
          JOIN USUARIOS U ON U.ID_USUARIO=P.CLIENTE_USUARIO_FK
          WHERE P.RECEBEDOR_USUARIO_FK=@id AND P.METODO='PRESENCIAL'
            AND P.STATUS='PENDENTE' AND A.STATUS_AGENDAMENTO<>'CANCELADO'
          ORDER BY A.DATA_AGENDAMENTO DESC LIMIT 100",conn);
        cmd.Parameters.AddWithValue("id",recebedor);
        var itens=new List<object>();
        await using var rd=await cmd.ExecuteReaderAsync();
        while(await rd.ReadAsync())
            itens.Add(new { AgendamentoId=rd.GetInt32(0), Valor=rd.GetInt64(1)/100m,
                DataAgendamento=rd.GetDateTime(2), Cliente=rd.GetString(3)});
        return Ok(itens);
    }

    [HttpPatch("agendamento/{id:int}/confirmar-presencial")]
    [Authorize(Roles="EMPRESA,AUTONOMO")]
    public async Task<IActionResult> ConfirmarPresencial(int id)
    {
        if(!User.TryGetUsuarioId(out var recebedor)) return Unauthorized();
        await using var conn=await OpenAsync();
        // Apenas o recebedor associado à reserva pode confirmar o dinheiro em espécie,
        // PIX ou cartão presencialmente. Nenhuma operação Stripe é executada aqui.
        await using var cmd=new NpgsqlCommand(@"
          UPDATE PAGAMENTOS_AGENDAMENTO P SET STATUS='PAGO',ATUALIZADO_EM=NOW()
          FROM AGENDAMENTOS A
          WHERE P.AGENDAMENTO_FK=@id AND A.ID_AGENDAMENTO=P.AGENDAMENTO_FK
            AND P.RECEBEDOR_USUARIO_FK=@recebedor
            AND P.METODO='PRESENCIAL' AND P.STATUS='PENDENTE'
            AND A.STATUS_AGENDAMENTO <> 'CANCELADO'",conn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("recebedor",recebedor);
        return await cmd.ExecuteNonQueryAsync() == 1 ? NoContent() :
            Conflict(new {erro="Pagamento não encontrado ou já confirmado."});
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        var signature=Request.Headers["Stripe-Signature"].ToString();
        if(string.IsNullOrEmpty(signature)) return BadRequest();
        var json=await new StreamReader(Request.Body).ReadToEndAsync();
        Event stripeEvent;
        try
        {
            stripeEvent=EventUtility.ConstructEvent(json,signature,Required("PagamentoAgendamento:WebhookSecret"));
        }
        catch(StripeException)
        {
            return BadRequest(new {erro="Assinatura do evento inválida."});
        }
        if(stripeEvent.Data.Object is not Session session || session.Mode!="payment")
            return Ok();
        if(stripeEvent.Type is not ("checkout.session.completed" or
              "checkout.session.async_payment_succeeded" or "checkout.session.async_payment_failed"))
            return Ok();
        if(!int.TryParse(session.ClientReferenceId,out var agendamentoId))
            return Ok();
        var paid=session.PaymentStatus=="paid" ||
            stripeEvent.Type=="checkout.session.async_payment_succeeded";
        var failed=stripeEvent.Type=="checkout.session.async_payment_failed";
        if(!paid && !failed) return Ok();
        await using var conn=await OpenAsync();
        await using var tx=await conn.BeginTransactionAsync();
        await using(var processed=new NpgsqlCommand(@"
          INSERT INTO PAGAMENTOS_AGENDAMENTO_WEBHOOKS(EVENT_ID) VALUES(@id)
          ON CONFLICT DO NOTHING",conn,tx))
        {
            processed.Parameters.AddWithValue("id",stripeEvent.Id);
            if(await processed.ExecuteNonQueryAsync()==0) return Ok();
        }
        await using(var update=new NpgsqlCommand(@"
          UPDATE PAGAMENTOS_AGENDAMENTO SET
            STATUS=CASE WHEN @pago THEN 'PAGO' ELSE 'FALHOU' END,
            STRIPE_PAYMENT_INTENT_ID=@intent,
            ATUALIZADO_EM=NOW()
          WHERE AGENDAMENTO_FK=@agendamento
            AND STRIPE_SESSION_ID=@session
            AND METODO='ONLINE' AND STATUS='PENDENTE'
            AND VALOR_CENTAVOS=@valor",conn,tx))
        {
            update.Parameters.AddWithValue("pago",paid);
            update.Parameters.AddWithValue("intent",(object?)session.PaymentIntentId ?? DBNull.Value);
            update.Parameters.AddWithValue("agendamento",agendamentoId);
            update.Parameters.AddWithValue("session",session.Id);
            update.Parameters.AddWithValue("valor",session.AmountTotal ?? 0L);
            var updated=await update.ExecuteNonQueryAsync();
            if(updated!=1)
                _logger.LogWarning("Evento Stripe sem pagamento pendente correspondente. EventId={EventId}",stripeEvent.Id);
        }
        await tx.CommitAsync();
        return Ok();
    }
}
