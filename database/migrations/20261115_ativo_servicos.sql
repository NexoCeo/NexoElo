-- Serviços podem ser ocultados da agenda sem excluir cadastro ou histórico.
ALTER TABLE SERVICOS ADD COLUMN IF NOT EXISTS ATIVO BOOLEAN NOT NULL DEFAULT TRUE;
CREATE INDEX IF NOT EXISTS idx_servicos_usuario_ativo ON SERVICOS (USUARIO_FK, ATIVO);
