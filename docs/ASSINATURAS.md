# Assinaturas — configuração de produção

## Planos do NexoElo

| Plano | Preço mensal | Serviços | Profissionais | Relatórios |
| --- | ---: | ---: | ---: | --- |
| Grátis (30 dias) | R$ 0 | Ilimitados | Ilimitados | Sim |
| Básico | R$ 29,99 | 4 | 2 | Não |
| Intermediário | R$ 49,99 | 8 | 4 | Sim |
| Avançado | R$ 99,99 | Ilimitados | Ilimitados | Sim |

O período gratuito é calculado a partir de `USUARIOS.DATA_CRIACAO_USUARIO`, sem solicitar cartão. Somente EMPRESA e AUTONOMO precisam de assinatura individual.

## Configuração no Render

Crie, **no Stripe**, três produtos com preços recorrentes mensais em BRL exatamente nos valores acima. Preencha as variáveis de ambiente abaixo com os IDs `price_...` retornados pelo Stripe:

- `Stripe__Prices__BASICO`
- `Stripe__Prices__INTERMEDIARIO`
- `Stripe__Prices__AVANCADO`
- `Stripe__SecretKey`
- `Stripe__WebhookSecret`
- `Stripe__SuccessUrl`
- `Stripe__CancelUrl`
- `Stripe__PortalReturnUrl`

Configure o endpoint de webhook em `https://nexoelo.onrender.com/api/Assinatura/webhook` e habilite os eventos `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated` e `customer.subscription.deleted`. Nunca envie a chave secreta Stripe para o frontend.

O portal Stripe precisa permitir atualizações de assinatura, caso deseje que o cliente troque planos por ele. A atualização do plano somente é reconhecida após o webhook assinado.

## Observações operacionais

- `GET /api/Assinatura/minha` retorna o plano e os limites calculados pela API autenticada.
- `POST /api/Assinatura/checkout` recebe `{ "UsuarioId": 1, "Plano": "BASICO" }` (ou INTERMEDIARIO/AVANCADO). O valor enviado pelo cliente não determina o preço; a API usa os IDs configurados no servidor.
- O bloqueio de alterações operacionais é aplicado no backend, além do bloqueio visual no React.
- O registro de serviços e a aprovação de profissionais são restringidos pelos limites do plano.
- Os registros existentes **não são excluídos** ao reduzir o plano. A seleção explícita de recursos ativos e a suspensão dos excedentes continuam pendentes, exigindo tratamento conjunto de persistência e disponibilidade pública.
- Ao cancelar, o plano mantém acesso até a data de término informada pela Stripe quando aplicável.
- **Não liberar uma cobrança real sem executar testes integrados de checkout/webhook, upgrade/downgrade e concorrência**.

## Compatibilidade

Assinaturas antigas cujo `STRIPE_PRICE_ID` não corresponda a um dos três IDs configurados serão classificadas como `SEM_PLANO`. Faça o mapeamento/migração de clientes antigos antes de habilitar cobrança para a base de produção.

## Downgrade: selecao nao destrutiva de recursos

**Migration obrigatoria antes do primeiro downgrade:**
`database/migrations/20261015_selecao_recursos_downgrade.sql`.
A migration cria `ASSINATURA_RECURSOS_ATIVOS` no PostgreSQL. Ela nao e aplicada automaticamente pelo deploy; executar com o cliente SQL conectado ao banco correto. A API bloqueia a troca de plano enquanto a tabela nao existir.

- A pagina Minha assinatura lista recursos e solicita a escolha quando servicos ou profissionais ultrapassarem os limites do plano de destino.
- `GET /api/Assinatura/recursos` lista todos os recursos de propriedade da conta com estado `Ativo`.
- `POST /api/Assinatura/alterar-plano` recebe `ServicosAtivos` e `ProfissionaisAtivos` com os IDs dos recursos escolhidos (exatamente o limite quando ha excedente).
- `PUT /api/Assinatura/recursos` permite refazer a selecao dentro do plano vigente sem modificar preco.
- As selecoes persistidas filtram a listagem publica de servicos, os servicos por profissional, a disponibilidade e a criacao de novos agendamentos.
- Agendamentos historicos continuam no banco. Recursos excedentes nao sao excluidos.
- Atualizacao do Stripe e gravacao no PostgreSQL nao formam transacao distribuida. Em caso de falha entre as etapas, o endpoint de regularizacao permite reconstruir a selecao. Nao excluir registros manualmente.
- Para planos sem excedentes, a selecao restritiva anterior e removida.

**Recomendado antes de producao:** testar simultaneidade de alteracao de plano, associacao de profissionais e criacao de agendamentos; reforcar serializacao no banco e reconciliacao Stripe/PostgreSQL para eventos concorrentes.
