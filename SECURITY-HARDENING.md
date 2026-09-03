# Configuracao de seguranca

## Ordem de deploy

1. Execute `SaaS.Infrastructure/Scripts/2026-08-11-hardening-recuperacao-senha.sql` no PostgreSQL.
2. Configure as variaveis de ambiente abaixo no Render.
3. Gere valores novos para todos os segredos que ja estiveram versionados.
4. Publique o backend e depois o frontend.
5. Confirme login, recuperacao de senha, criacao de profissional e agendamento publico.

## Variaveis do backend

- `ConnectionStrings__DefaultConnection` (aceita o formato Npgsql ou a URL `postgresql://...` fornecida pelo Render)
- `Jwt__Key` (valor aleatorio com pelo menos 32 bytes)
- `Jwt__Issuer`
- `Jwt__Audience`
- `PasswordRecovery__Pepper` (valor aleatorio com pelo menos 32 bytes)
- `Email__Name`
- `Email__SmtpHost`
- `Email__SmtpPort`
- `Email__Username`
- `Email__Password`
- `Email__From`
- `Stripe__SecretKey`
- `Stripe__WebhookSecret`
- `Stripe__DefaultPriceId`
- `Stripe__SuccessUrl`
- `Stripe__CancelUrl`
- `Stripe__PortalReturnUrl`
- `App__FrontendBaseUrl`
- `App__AgendamentoPath`
- `Cors__AllowedOrigins__0` (adicione outros indices quando necessario)

## Variavel do frontend

- `VITE_API_ORIGIN`

## Rotacao obrigatoria

O historico Git anterior continha configuracoes sensiveis. Remover os valores do arquivo atual nao invalida copias antigas. Rotacione a senha do banco, a chave JWT, as credenciais SMTP, a chave secreta e o segredo de webhook da Stripe. Tokens JWT emitidos com a chave antiga devem deixar de ser aceitos.

Arquivos em `bin`, `obj` e `wwwroot/uploads` nao devem voltar ao repositorio. Uploads de producao precisam ser armazenados em volume persistente ou object storage fora do Git.
