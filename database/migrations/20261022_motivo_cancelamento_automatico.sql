-- Run before deploying the backend that reads/writes MOTIVO_CANCELAMENTO.
ALTER TABLE AGENDAMENTOS
    ADD COLUMN IF NOT EXISTS MOTIVO_CANCELAMENTO VARCHAR(255);

-- Preserve completed and manually cancelled appointments; expire only overdue scheduled ones.
UPDATE AGENDAMENTOS
SET STATUS_AGENDAMENTO = 'CANCELADO',
    MOTIVO_CANCELAMENTO = 'Cancelamento automático de agendamento!'
WHERE UPPER(TRIM(STATUS_AGENDAMENTO)) = 'AGENDADO'
  AND DATA_AGENDAMENTO <= (CURRENT_TIMESTAMP AT TIME ZONE 'America/Sao_Paulo') - INTERVAL '24 hours';
