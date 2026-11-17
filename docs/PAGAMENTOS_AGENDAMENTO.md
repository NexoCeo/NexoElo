# Pagamentos de agendamentos — módulo isolado

## Escopo
- Assinaturas permanecem em `api/Assinatura` e não foram modificadas.
- Pagamentos de serviços usam `api/pagamentos-agendamento`, com Stripe Connect Accounts v2 (`POST /v2/core/accounts`, dashboard Full e configuração Merchant) como conta recebedora própria para EMPRESA/AUTONOMO.
- Padrão: PRESENCIAL. As modalidades ONLINE e AMBOS somente podem ser ativadas após a Stripe habilitar cobranças e repasses na conta conectada.
- Pagamentos online usam **direct charges**: o checkout é criado na conta conectada (`Stripe-Account`), sem `transfer_data` nem comissão de aplicação. O estabelecimento recebe o saldo do pagamento diretamente em sua Stripe, descontada a tarifa efetiva da Stripe, compensada pela linha adicional exibida e cobrada do cliente final.
- O checkout online aceita cartão; pagamento presencial não sofre acréscimo pela plataforma.
- O backend calcula o total em centavos por gross-up: `ceil((valor_servico_centavos + tarifa_fixa_centavos) * 10000 / (10000 - tarifa_percentual_bps))`.
- A precificação padrão de referência é **3,99% + R$ 0,39** (cartão nacional). Opcionalmente ajustar `PagamentoAgendamento__TarifaCartaoBps` e `PagamentoAgendamento__TarifaCartaoFixaCentavos` no Render à tarifa efetivamente contratada.
- **Atenção financeira:** o custo extra é uma estimativa baseada nas taxas configuradas; não garante líquido exato para tarifas diferentes, cartões internacionais ou contestações. Para direct charges a Stripe debita taxas do estabelecimento; acompanhar o saldo líquido real e validar regras para acréscimos de processamento antes da produção.

## 1. Banco (Neon)
Execute manualmente **antes do deploy**:
`database/migrations/20261116_pagamentos_agendamento.sql`

Não altere tabelas de assinatura. As tabelas novas são:
`CONFIGURACOES_PAGAMENTO`, `PAGAMENTOS_AGENDAMENTO`,
`PAGAMENTOS_AGENDAMENTO_WEBHOOKS`.

## 2. Render — configuração de ambiente
- Não é necessária a variável `PagamentoAgendamento__FrontendBaseUrl`. A API utiliza a origem de produção `https://nexoelo.nexoceo.com.br` já cadastrada em `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1` ou outra posição do array `Cors:AllowedOrigins`.
- Garanta que o domínio esteja presente nessa lista de origens autorizadas; nenhuma variável de CORS precisa ser duplicada.
- `PagamentoAgendamento__WebhookSecret=whsec_...` (segredo do webhook antigo da plataforma; manter se já utiliza esse endpoint).
- `PagamentoAgendamento__ConnectWebhookSecret=whsec_...` (segredo NOVO do webhook **Connected accounts** para direct charges, específico dos agendamentos).
- A API reutiliza `Stripe__SecretKey` da conexão técnica Stripe já configurada.
- Não alterar `Stripe__WebhookSecret` nem o endpoint das assinaturas.

## 3. Painel Stripe
1. Ative Stripe Connect para a plataforma e verifique a elegibilidade de contas Express no Brasil. A criação das contas usa Accounts v2; não é necessário habilitar a criação legada Accounts v1.
2. Configure no Stripe Workbench um **destino de eventos de contas conectadas (Connected accounts)** para pagamentos dos agendamentos:
   `https://nexoelo.onrender.com/api/pagamentos-agendamento/webhook-connect`
   Preserve o webhook anterior da plataforma se ele já existe: `/api/pagamentos-agendamento/webhook`.
3. Selecione os eventos:
   - `checkout.session.completed`
   - `checkout.session.async_payment_succeeded`
   - `checkout.session.async_payment_failed`
4. Copie o signing secret do **novo endpoint Connected accounts** para `PagamentoAgendamento__ConnectWebhookSecret`; não substitua segredos das assinaturas.
5. Teste com chaves sandbox e contas conectadas em sandbox, antes de habilitar cobrança real.

## 4. Operação
- Empresa/autônomo: menu **Pagamentos** -> conectar conta Stripe Merchant (dashboard completo) -> completar onboarding -> escolher PRESENCIAL, ONLINE ou AMBOS. A Stripe gerencia responsabilidade por perdas (`losses_collector=stripe`) e recebe as tarifas (`fees_collector=stripe`).
- Cliente: escolhe modalidade na confirmação do agendamento.
- PRESENCIAL: reserva confirmada e pagamento fica PENDENTE; estabelecimento confirma manualmente o recebimento na própria página Pagamentos.
- ONLINE: reserva criada e Checkout iniciado; pagamento fica PENDENTE até confirmação do webhook assinado. Se o checkout falhar ou for abandonado, o cliente pode retomá-lo pelo histórico.
- A API calcula o valor dos serviços no banco, nunca utiliza preços enviados pelo navegador. A prévia no frontend utiliza a mesma fórmula, mas não é fonte de confiança para a cobrança.
- O total da cobrança é processado diretamente pela Stripe do prestador, com acréscimo separado para o cliente. O valor líquido do prestador é o total menos a tarifa efetiva cobrada pela Stripe; a tarifa estimada não garante coincidência exata em todos os métodos e cartões.
- O usuário que realiza o checkout deve ser o cliente proprietário da reserva; o recebedor é derivado do serviço vinculado à empresa/autônomo.
- Nenhum pagamento de serviço é tratado pelos webhooks ou tabelas de assinatura.

## 5. Limitações antes de produção
- O status do **agendamento** e do **pagamento** são independentes.
- O registro presencial é declaração do estabelecimento, não liquidação Stripe.
- O cancelamento de uma reserva **não faz estorno automático**. É necessário adicionar fluxo de reembolso e reconciliação antes de permitir cancelamento online pago em produção.
- Uma reserva é criada antes do checkout; horários são bloqueados mesmo se o cliente abandonar a página. Definir regra de expiração de pagamento online e política de reserva.
- Confirmar tratamento de PIX e métodos disponíveis em cada conta conectada: Stripe disponibiliza conforme país, conta e configuração.
- Os testes automatizados validam a compilação e fluxos simulados. Simular webhooks repetidos, atrasados, falhas de conexão, contas sem habilitação e pagamentos reais em sandbox antes de liberar produção.

## 6. Importante: migração do modelo Connect
- Antes: recipient/Express com `destination charges` e responsabilidade por perdas da plataforma. Nova configuração: merchant/Full com **direct charges** e responsabilidades geridas pela Stripe. A alteração é necessária porque o ambiente Stripe recusou `losses_collector=application`.
- Contas de recebedores criadas no modelo antigo precisam de análise e migração específica antes de processar novos pagamentos. Não reutilizar automaticamente contas com configuração `recipient` em checkouts diretos.
- O webhook de contas conectadas é obrigatório: sem ele, a confirmação de pagamento no NexoElo não será atualizada.
