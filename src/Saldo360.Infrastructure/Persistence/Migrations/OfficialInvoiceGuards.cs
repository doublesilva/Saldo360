namespace Saldo360.Infrastructure.Persistence.Migrations;

internal static class OfficialInvoiceGuards
{
    internal const string Up = """
        CREATE TRIGGER saldo360_financial_write_guard BEFORE INSERT OR UPDATE OR DELETE
        ON financial_credit_card_invoice_consolidations FOR EACH ROW EXECUTE FUNCTION saldo360_financial_write_guard();

        CREATE FUNCTION saldo360_official_component_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE r jsonb; w uuid; invoice uuid; version integer; candidate uuid;
        BEGIN
            FOR r IN SELECT to_jsonb(OLD) WHERE TG_OP <> 'INSERT' UNION ALL SELECT to_jsonb(NEW) WHERE TG_OP <> 'DELETE' LOOP
                w := (r->>'workspace_id')::uuid;
                IF current_setting('saldo360.resetting_workspace', true) = w::text THEN CONTINUE; END IF;
                IF TG_TABLE_NAME = 'financial_credit_card_purchases' THEN
                    FOR candidate IN SELECT DISTINCT i.invoice_id FROM financial_credit_card_installments i WHERE i.workspace_id=w AND i.purchase_id=(r->>'id')::uuid AND i.invoice_id IS NOT NULL LOOP
                        SELECT official_version INTO version FROM financial_credit_card_invoices WHERE workspace_id=w AND id=candidate FOR UPDATE;
                        IF version > 0 AND current_setting('saldo360.official_invoice', true) IS DISTINCT FROM candidate::text THEN RAISE EXCEPTION 'OFFICIAL_INVOICE_IMMUTABLE' USING ERRCODE='23514'; END IF;
                    END LOOP;
                ELSE
                    invoice := (r->>'invoice_id')::uuid;
                    IF invoice IS NULL AND TG_TABLE_NAME = 'financial_credit_card_imported_installments' THEN
                        SELECT id INTO invoice FROM financial_credit_card_invoices WHERE workspace_id=w AND credit_card_id=(r->>'credit_card_id')::uuid AND competence=left(r->>'competence',7);
                    END IF;
                    SELECT official_version INTO version FROM financial_credit_card_invoices WHERE workspace_id=w AND id=invoice FOR UPDATE;
                    IF version > 0 AND current_setting('saldo360.official_invoice', true) IS DISTINCT FROM invoice::text THEN
                        RAISE EXCEPTION 'OFFICIAL_INVOICE_IMMUTABLE' USING ERRCODE='23514';
                    END IF;
                END IF;
            END LOOP;
            IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
        END $guard$;
        CREATE TRIGGER zz_official_component_guard BEFORE INSERT OR UPDATE OR DELETE ON financial_credit_card_installments FOR EACH ROW EXECUTE FUNCTION saldo360_official_component_guard();
        CREATE TRIGGER zz_official_component_guard BEFORE INSERT OR UPDATE OR DELETE ON financial_credit_card_imported_installments FOR EACH ROW EXECUTE FUNCTION saldo360_official_component_guard();
        CREATE TRIGGER zz_official_component_guard BEFORE INSERT OR UPDATE OR DELETE ON financial_credit_card_invoice_adjustments FOR EACH ROW EXECUTE FUNCTION saldo360_official_component_guard();
        CREATE TRIGGER zz_official_component_guard BEFORE UPDATE OR DELETE ON financial_credit_card_purchases FOR EACH ROW EXECUTE FUNCTION saldo360_official_component_guard();

        CREATE FUNCTION saldo360_official_version_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE w uuid; invoice uuid; current_invoice record; total numeric;
        BEGIN
            IF TG_OP='DELETE' THEN w:=OLD.workspace_id; ELSE w:=NEW.workspace_id; END IF;
            IF current_setting('saldo360.resetting_workspace', true) = w::text THEN
                IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
            END IF;
            IF TG_TABLE_NAME='financial_credit_card_invoice_consolidations' THEN
                IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'OFFICIAL_AUDIT_IMMUTABLE' USING ERRCODE='23514'; END IF;
                invoice:=NEW.invoice_id;
            ELSE invoice:=NEW.id;
            END IF;
            SELECT * INTO current_invoice FROM financial_credit_card_invoices WHERE workspace_id=w AND id=invoice;
            IF current_invoice.official_version > 0 THEN
                IF NOT EXISTS (SELECT 1 FROM financial_credit_card_invoice_consolidations v WHERE v.workspace_id=w AND v.invoice_id=invoice AND v.credit_card_id=current_invoice.credit_card_id AND v.version=current_invoice.official_version AND v.official_total=current_invoice.official_total) THEN
                    RAISE EXCEPTION 'OFFICIAL_CONSOLIDATION_PROOF_REQUIRED' USING ERRCODE='23514';
                END IF;
                SELECT COALESCE((SELECT SUM(i.amount) FROM financial_credit_card_installments i JOIN financial_credit_card_purchases p ON p.workspace_id=i.workspace_id AND p.id=i.purchase_id WHERE i.workspace_id=w AND i.invoice_id=invoice AND i.status='Active' AND p.status='Active'),0)
                     + COALESCE((SELECT SUM(i.amount) FROM financial_credit_card_imported_installments i WHERE i.workspace_id=w AND i.invoice_id=invoice AND i.is_historical AND i.status='Active'),0)
                     + COALESCE((SELECT SUM(CASE WHEN a.nature='Debit' THEN a.amount ELSE -a.amount END) FROM financial_credit_card_invoice_adjustments a WHERE a.workspace_id=w AND a.invoice_id=invoice AND a.status='Active'),0) INTO total;
                IF total <> current_invoice.official_total THEN RAISE EXCEPTION 'OFFICIAL_COMPOSITION_TOTAL_MISMATCH' USING ERRCODE='23514'; END IF;
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
        END $guard$;
        CREATE CONSTRAINT TRIGGER official_version_proof AFTER INSERT OR UPDATE ON financial_credit_card_invoices DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION saldo360_official_version_guard();
        CREATE CONSTRAINT TRIGGER official_audit_proof AFTER INSERT OR UPDATE OR DELETE ON financial_credit_card_invoice_consolidations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION saldo360_official_version_guard();

        CREATE FUNCTION saldo360_official_state_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
            IF NEW.official_version < OLD.official_version OR NEW.official_version > OLD.official_version+1
               OR (OLD.official_version > 0 AND NEW.official_version=OLD.official_version AND (NEW.official_total IS DISTINCT FROM OLD.official_total OR NEW.official_closed_at IS DISTINCT FROM OLD.official_closed_at
                   OR NEW.competence<>OLD.competence OR NEW.closing_date<>OLD.closing_date OR NEW.due_date<>OLD.due_date)) THEN
                RAISE EXCEPTION 'OFFICIAL_VERSION_IMMUTABLE' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $guard$;
        CREATE TRIGGER zz_official_state_guard BEFORE UPDATE ON financial_credit_card_invoices FOR EACH ROW EXECUTE FUNCTION saldo360_official_state_guard();

        CREATE FUNCTION saldo360_official_document_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
            IF current_setting('saldo360.resetting_workspace', true) = OLD.workspace_id::text THEN RETURN NEW; END IF;
            IF EXISTS(SELECT 1 FROM financial_credit_card_invoice_consolidations v WHERE v.workspace_id=OLD.workspace_id AND v.document_id=OLD.id)
                AND OLD.status='Imported' AND (NEW.status IS DISTINCT FROM OLD.status OR NEW.statement_analysis_json IS DISTINCT FROM OLD.statement_analysis_json
                    OR NEW.credit_card_id IS DISTINCT FROM OLD.credit_card_id OR NEW.competence IS DISTINCT FROM OLD.competence
                    OR NEW.content_hash IS DISTINCT FROM OLD.content_hash OR NEW.storage_reference IS DISTINCT FROM OLD.storage_reference) THEN
                RAISE EXCEPTION 'OFFICIAL_DOCUMENT_IMMUTABLE' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $guard$;
        CREATE TRIGGER zz_official_document_guard BEFORE UPDATE ON financial_document_inbox_items FOR EACH ROW EXECUTE FUNCTION saldo360_official_document_guard();

        CREATE FUNCTION saldo360_official_payment_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE version integer;
        BEGIN
            SELECT official_version INTO version FROM financial_credit_card_invoices WHERE workspace_id=NEW.workspace_id AND id=NEW.invoice_id FOR UPDATE;
            IF version IS NULL OR version=0 THEN RAISE EXCEPTION 'OFFICIAL_INVOICE_REQUIRED_FOR_PAYMENT' USING ERRCODE='23514'; END IF;
            RETURN NEW;
        END $guard$;
        CREATE TRIGGER zz_official_payment_guard BEFORE INSERT ON financial_credit_card_invoice_payments FOR EACH ROW EXECUTE FUNCTION saldo360_official_payment_guard();
        """;
    internal const string Down = """
        DO $guard$ BEGIN IF EXISTS(SELECT 1 FROM financial_credit_card_invoice_consolidations) THEN RAISE EXCEPTION 'Official versions must be preserved; downgrade refused'; END IF; END $guard$;
        DROP TRIGGER zz_official_payment_guard ON financial_credit_card_invoice_payments;
        DROP FUNCTION saldo360_official_payment_guard();
        DROP TRIGGER zz_official_state_guard ON financial_credit_card_invoices;
        DROP FUNCTION saldo360_official_state_guard();
        DROP TRIGGER zz_official_document_guard ON financial_document_inbox_items;
        DROP FUNCTION saldo360_official_document_guard();
        DROP TRIGGER official_version_proof ON financial_credit_card_invoices;
        DROP TRIGGER official_audit_proof ON financial_credit_card_invoice_consolidations;
        DROP FUNCTION saldo360_official_version_guard();
        DROP TRIGGER zz_official_component_guard ON financial_credit_card_installments;
        DROP TRIGGER zz_official_component_guard ON financial_credit_card_imported_installments;
        DROP TRIGGER zz_official_component_guard ON financial_credit_card_invoice_adjustments;
        DROP TRIGGER zz_official_component_guard ON financial_credit_card_purchases;
        DROP FUNCTION saldo360_official_component_guard();
        """;
}
