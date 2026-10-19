-- Executar no PostgreSQL antes de habilitar o Embedded Signup.
CREATE TABLE IF NOT EXISTS whatsapp_conexoes (
 usuario_id integer PRIMARY KEY REFERENCES usuarios(id_usuario) ON DELETE CASCADE,
 waba_id text NOT NULL,
 phone_number_id text NOT NULL UNIQUE,
 numero text,
 nome_exibicao text,
 token_cifrado text NOT NULL,
 status text NOT NULL DEFAULT 'PENDENTE',
 confirmacoes boolean NOT NULL DEFAULT false,
 lembretes boolean NOT NULL DEFAULT false,
 promocoes boolean NOT NULL DEFAULT false,
 horas_antecedencia integer NOT NULL DEFAULT 24 CHECK (horas_antecedencia BETWEEN 1 AND 168),
 limite_mensal_reais numeric(12,2) NOT NULL DEFAULT 50 CHECK (limite_mensal_reais >= 0),
 atualizado_em timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS whatsapp_eventos (
 id bigserial PRIMARY KEY,
 identificador text NOT NULL UNIQUE,
 phone_number_id text,
 wamid text,
 tipo text NOT NULL,
 estado text,
 recebido_em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_whatsapp_eventos_phone_data ON whatsapp_eventos(phone_number_id, recebido_em DESC);
