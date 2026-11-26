-- Удаление документа администратором (ТЗ 7.9): документ, на который не ссылается ни одна опубликованная версия
-- другого документа, получает статус «удалён» — пропадает из поиска, дерева и списков, путь в проекте освобождается.
-- Версии, ссылки и журнал не удаляются: опубликованное только дополняется (ТЗ, раздел 9).
ALTER TABLE documents DROP CONSTRAINT documents_status_check;
ALTER TABLE documents ADD CONSTRAINT documents_status_check CHECK (status IN ('active', 'unused', 'deleted'));
