# Pagamentos de agendamentos — módulo isolado

## Escopo
- Assinaturas permanecem em `api/Assinatura` e não foram modificadas.
- Pagamentos de serviços usam `api/pagamentos-agendamento`, com Stripe Connect Express como conta recebedora própria para EMPRESA/AUTONOMO.
- Padrão: PRESENCIAL. As modalidades ONLINE e AMBOS somente podem ser ativadas após a Stripe habilitar cobranças e repasses na conta conectada.
- Pagamentos online usam Stripe Checkout em modo `payment` e `payment_intent_data.transfer_data.destination`, direcionando o recebimento ao estabelecimento.
- **Atenção financeira:** nesta configuração de *destination charges*, a plataforma responde pelas taxas da Stripe/chargebacks conforme o modelo Connect contratado, mesmo quando o pagamento é transferido integralmente ao estabelecimento. Revisar custos e responsabilidade antes de ativar contas reais.

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
1. Ative Stripe Connect para a plataforma e verifique a elegibilidade de contas Express no Brasil.
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
- A API calcula o valor dos serviços no banco, nunca utiliza preços enviados pelo navegador.
- O usuário que realiza o checkout deve ser o cliente proprietário da reserva; o recebedor é derivado do serviço vinculado à empresa/autônomo.
- Nenhum pagamento de serviço é tratado pelos webhooks ou tabelas de assinatura.

## 5. Limitações antes de produção
- O status do **agendamento** e do **pagamento** são independentes.
- O registro presencial é declaração do estabelecimento, não liquidação Stripe.
- O cancelamento de uma reserva **não faz estorno automático**. É necessário adicionar fluxo de reembolso e reconciliação antes de permitir cancelamento online pago em produção.
- Uma reserva é criada antes do checkout; horários são bloqueados mesmo se o cliente abandonar a página. Definir regra de expiração de pagamento online e política de reserva.
- Confirmar tratamento de PIX e métodos disponíveis em cada conta conectada: Stripe disponibiliza conforme país, conta e configuração.
- Os testes automatizados validam a compilação e fluxos simulados. Simular webhooks repetidos, atrasados, falhas de conexão, contas sem habilitação e pagamentos reais em sandbox antes de liberar produção.
