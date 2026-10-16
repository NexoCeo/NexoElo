# WhatsApp Cloud API — etapa 1 (integração protegida)

Estado atual: **infraestrutura inicial; nenhuma mensagem é enviada**.

## Rotas

- `GET /api/WhatsappBusiness/status`: exige autenticação EMPRESA/AUTONOMO e assinatura AVANCADO ativa. Mostra estado NAO_CONFIGURADO.
- `GET /api/WhatsappBusiness/signup-config`: bloqueado com HTTP 503 até backend estar pronto para armazenar os tokens.
- `POST /api/WhatsappBusiness/conectar`, `PUT /preferencias`, `POST /desconectar`: bloqueados HTTP 503 até implementação segura.
- `GET /api/WhatsappBusiness/webhook`: valida desafio de configuração enviado pela Meta.
- `POST /api/WhatsappBusiness/webhook`: verifica X-Hub-Signature-256 com comparação de tempo constante. Retorna 503 mesmo com assinatura válida até ser implementado armazenamento idempotente de eventos.

## Variáveis (Render, nunca no React ou repositório)

- `WhatsApp__AppId`: identificador da aplicação Meta
- `WhatsApp__ConfigurationId`: configuração Embedded Signup
- `WhatsApp__AppSecret`: segredo para troca do código e validação dos webhooks
- `WhatsApp__WebhookVerifyToken`: token aleatório para confirmação do callback
- Futuramente `WhatsApp__GraphVersion` explicitamente aprovada após validar a documentação da Meta.

O callback de webhooks previsto é `https://nexoelo.onrender.com/api/WhatsappBusiness/webhook`, sujeito à configuração de domínio e roteamento.

## Próximas etapas antes de ativar

1. Criar migrations Postgres para vínculo entre proprietário (tenant), WABA, phone number ID, credencial criptografada, consentimentos, templates e log de mensagens com idempotência.
2. Trocar o código de autorização do Embedded Signup por credencial adequada via Graph API exclusivamente no backend, validando WABA e número e associação exclusiva a um tenant.
3. Verificar permissões da aplicação Meta, inscrição de webhooks em WABA, capacidade de envio e estado financeiro sem assumir que pagamento via cartão é sempre embutido.
4. Implementar consumidor de webhooks persistente, controle de status e reprocessamento idempotente; só então retornar 200.
5. Criar templates aprovados, fila/outbox de confirmações e lembretes com consentimento, limites, controle de custos e tratamento de falhas.
6. Integrar indicadores reais ao frontend, configurar alarmes e realizar testes de sandbox/cobrança.

**Não habilitar disparos nem exibir status 'conectado' apenas por uma autorização de login.**