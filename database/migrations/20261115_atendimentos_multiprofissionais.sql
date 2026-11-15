-- Complementa a migration de multiplos servicos, preservando dados existentes.
-- Cada item do atendimento pode ser atribuido a um profissional diferente.
ALTER TABLE AGENDAMENTO_SERVICOS
    ADD COLUMN IF NOT EXISTS PROFISSIONAL_FK INTEGER REFERENCES USUARIOS(ID_USUARIO),
    ADD COLUMN IF NOT EXISTS INICIO_ATENDIMENTO TIMESTAMP;

CREATE INDEX IF NOT EXISTS idx_agendamento_servicos_profissional_horario
    ON AGENDAMENTO_SERVICOS(PROFISSIONAL_FK, INICIO_ATENDIMENTO)
    WHERE PROFISSIONAL_FK IS NOT NULL;
