-- Executar antes da publicação da API com estados de vínculo adicionais.
-- Preserva os status anteriores e permite reativação sem excluir usuários.
DO $$
DECLARE restricao RECORD;
BEGIN
    FOR restricao IN
        SELECT c.conname
        FROM pg_constraint c
        WHERE c.conrelid = 'vinculos'::regclass
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%status_solicitacao%'
    LOOP
        EXECUTE format('ALTER TABLE VINCULOS DROP CONSTRAINT %I', restricao.conname);
    END LOOP;
END $$;

ALTER TABLE VINCULOS
    ADD CONSTRAINT vinculos_status_solicitacao_check
    CHECK (STATUS_SOLICITACAO IN (
        'NENHUM_VINCULO', 'PENDENTE', 'APROVADO', 'RECUSADO', 'INATIVO', 'DESVINCULADO'
    ));
