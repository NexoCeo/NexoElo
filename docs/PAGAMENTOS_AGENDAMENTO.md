# Pagamentos de agendamentos — módulo isolado

## Escopo
- Assinaturas permanecem em `api/Assinatura` e não foram modificadas.
- Pagamentos de serviços usam `api/pagamentos-agendamento`, com Stripe Connect Accounts v2 (`POST /v2/core/accounts`, dashboard Express) como conta recebedora própria para EMPRESA/AUTONOMO.
- Padrão: PRESENCIAL. As modalidades ONLINE e AMBOS somente podem ser ativadas após a Stripe habilitar cobranças e repasses na conta conectada.
- Pagamentos online usam Stripe Checkout em modo `payment` e `payment_intent_data.transfer_data.destination` com `transfer_data.amount` igual ao valor original do serviço. A cobrança inclui uma linha separada de custo de processamento paga pelo cliente final.
- O checkout online aceita cartão; pagamento presencial não sofre acréscimo pela plataforma.
- O backend calcula o total em centavos por gross-up: `ceil((valor_servico_centavos + tarifa_fixa_centavos) * 10000 / (10000 - tarifa_percentual_bps))`.
- A precificação padrão de referência é **3,99% + R$ 0,39** (cartão nacional). Opcionalmente ajustar `PagamentoAgendamento__TarifaCartaoBps` e `PagamentoAgendamento__TarifaCartaoFixaCentavos` no Render à tarifa efetivamente contratada.
- **Atenção financeira:** `destination charges` cobram taxas e eventuais disputas da conta da plataforma. O adicional cobrado ao cliente é uma estimativa, não uma transferência automática da tarifa da Stripe ao cliente. Cartões internacionais, métodos diferenciados, câmbio, disputas ou mudanças tarifárias podem gerar diferença a cargo da plataforma. Validar regras de cobrança adicional e política de reembolso antes da produção.

## 1. Banco (Neon)
Execute manualmente **antes do deploy**:
`database/migrations/20261116_pagamentos_agendamento.sql`

Não altere tabelas de assinatura. As tabelas novas são:
`CONFIGURACOES_PAGAMENTO`, `PAGAMENTOS_AGENDAMENTO`,
`PAGAMENTOS_AGENDAMENTO_WEBHOOKS`.

## 2. Render — configuração de ambiente
- Não é necessária a variável `PagamentoAgendamento__FrontendBaseUrl`. A API utiliza a origem de produção `https://nexoelo.nexoceo.com.br` já cadastrada em `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1` ou outra posição do array `Cors:AllowedOrigins`.
- Garanta que o domínio esteja presente nessa lista de origens autorizadas; nenhuma variável de CORS precisa ser duplicada.
- `PagamentoAgendamento__WebhookSecret=whsec_...` (segredo de **outro** endpoint webhook no painel Stripe).
- A API reutiliza `Stripe__SecretKey` da conexão técnica Stripe já configurada.
- Não alterar `Stripe__WebhookSecret` nem o endpoint das assinaturas.

## 3. Painel Stripe
1. Ative Stripe Connect para a plataforma e verifique a elegibilidade de contas Express no Brasil. A criação das contas usa Accounts v2; não é necessário habilitar a criação legada Accounts v1.
2. Configure um endpoint de webhook para **pagamentos de agendamentos**, separado do endpoint de assinatura:
   `https://nexoelo.onrender.com/api/pagamentos-agendamento/webhook`
3. Selecione os eventos:
   - `checkout.session.completed`
   - `checkout.session.async_payment_succeeded`
   - `checkout.session.async_payment_failed`
4. Copie o signing secret deste endpoint para `PagamentoAgendamento__WebhookSecret`.
5. Teste com chaves sandbox e contas conectadas em sandbox, antes de habilitar cobrança real.

## 4. Operação
- Empresa/autônomo: menu **Pagamentos** -> conectar conta Stripe -> completar onboarding -> escolher PRESENCIAL, ONLINE ou AMBOS.
- Cliente: escolhe modalidade na confirmação do agendamento.
- PRESENCIAL: reserva confirmada e pagamento fica PENDENTE; estabelecimento confirma manualmente o recebimento na própria página Pagamentos.
- ONLINE: reserva criada e Checkout iniciado; pagamento fica PENDENTE até confirmação do webhook assinado. Se o checkout falhar ou for abandonado, o cliente pode retomá-lo pelo histórico.
- A API calcula o valor dos serviços no banco, nunca utiliza preços enviados pelo navegador. A prévia no frontend utiliza a mesma fórmula, mas não é fonte de confiança para a cobrança.
- O valor enviado ao prestador é exatamente o valor dos serviços; o cliente final paga o total acrescido do custo de processamento informado antes do checkout.
- O usuário que realiza o checkout deve ser o cliente proprietário da reserva; o recebedor é derivado do serviço vinculado à empresa/autônomo.
- Nenhum pagamento de serviço é tratado pelos webhooks ou tabelas de assinatura.

## 5. Limitações antes de produção
- O status do **agendamento** e do **pagamento** são independentes.
- O registro presencial é declaração do estabelecimento, não liquidação Stripe.
- O cancelamento de uma reserva **não faz estorno automático**. É necessário adicionar fluxo de reembolso e reconciliação antes de permitir cancelamento online pago em produção.
- Uma reserva é criada antes do checkout; horários são bloqueados mesmo se o cliente abandonar a página. Definir regra de expiração de pagamento online e política de reserva.
- Confirmar tratamento de PIX e métodos disponíveis em cada conta conectada: Stripe disponibiliza conforme país, conta e configuração.
- Os testes automatizados validam a compilação e fluxos simulados. Simular webhooks repetidos, atrasados, falhas de conexão, contas sem habilitação e pagamentos reais em sandbox antes de liberar produção.
