# WhatsApp Cloud API — implementação incremental

## Estado atual

Foi implementado o Embedded Signup no backend, troca server-side do código Meta, verificação de que o número pertence à WABA autorizada, armazenamento cifrado de credenciais por estabelecimento, atualização de preferências e recebimento de webhooks assinados com persistência idempotente. **Envios automáticos e campanhas continuam desligados**; a conexão e as configurações não significam que mensagens possam ser disparadas.

## Pré-requisitos obrigatórios

1. **Aplicar manualmente** `docs/migrations/20261019_whatsapp.sql` no PostgreSQL de produção após revisar compatibilidade com as tabelas existentes.
2. No Render configurar:
   - `WhatsApp__AppId` e `WhatsApp__ConfigurationId`: IDs públicos do aplicativo e Embedded Signup
   - `WhatsApp__AppSecret`: App Secret real da Meta (privado)
   - `WhatsApp__WebhookVerifyToken`: token aleatório da verificação de webhook
   - `WhatsApp__EncryptionKey`: base64 de **32 bytes aleatórios**, independente do App Secret e da chave JWT
   - `WhatsApp__GraphVersion`: versão atual suportada da Graph API (validar na documentação da Meta)
   - `WhatsApp__Enabled=true` **somente após** migration, credenciais e revisão de segurança
3. Configurar a URL de callback `https://nexoelo.onrender.com/api/WhatsappBusiness/webhook` e subscrever o campo `messages` na Meta.
4. Configurar domínios permitidos, URLs de redirecionamento e a configuração correta do Embedded Signup (Tech Provider).

PowerShell 5.1 para gerar WhatsApp__EncryptionKey (não publicar o resultado no GitHub):

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
[Convert]::ToBase64String($bytes)
```

**Nunca trocar a chave de criptografia sem migrar os tokens previamente cifrados.**

## Limitações a concluir

- Não há envio de templates, worker/outbox de agendamento, catálogo de templates aprovados, nem campanhas. As preferências salvas ainda não desencadeiam mensagens.
- Campos de métricas retornam zero até a agregação ser implementada; não são indicadores de consumo real ou preços Meta.
- O módulo não comprova faturamento direto por estabelecimento, verificação empresarial, elegibilidade de coexistência de número nem assinatura de webhook por WABA.
- Revogar o token na Meta e excluir o vínculo remoto exige fluxo separado; hoje desconectar remove apenas a conexão armazenada no NexoElo.
- Antes de produção: testes reais com WABA e número autorizados, regras de consentimento, tratamento de duplicidade de signup e revalidação de tokens.
- Implementações e migration não passaram por build/integration tests neste ambiente.
