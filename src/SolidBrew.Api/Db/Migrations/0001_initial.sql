-- SolidBrew PDM: исходная схема «проект — документ — версия — ссылка — блокировка».
-- Выполняется под ролью-владельцем (pdm). Роль приложения pdm_app создаётся до миграций (см. Migrator).

CREATE TABLE users (
    id            uuid        PRIMARY KEY,
    login         text        NOT NULL CHECK (length(login) BETWEEN 1 AND 64),
    display_name  text        NOT NULL,
    password_hash text        NOT NULL,
    role          text        NOT NULL CHECK (role IN ('admin', 'designer', 'reader')),
    active        boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX users_login_key ON users (lower(login));

CREATE TABLE sessions (
    token_hash bytea       PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES users (id),
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL
);
CREATE INDEX sessions_user_idx ON sessions (user_id);

CREATE TABLE projects (
    id         uuid        PRIMARY KEY,
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    kind       text        NOT NULL CHECK (kind IN ('project', 'library')),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX projects_name_key ON projects (lower(name));

CREATE TABLE documents (
    id               uuid        PRIMARY KEY,
    project_id       uuid        NOT NULL REFERENCES projects (id),
    cad              text        NOT NULL,
    kind             text        NOT NULL CHECK (kind IN ('part', 'assembly', 'drawing', 'other')),
    current_rel_path text        NOT NULL,
    rel_key          text        NOT NULL,
    status           text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'unused')),
    created_by       uuid        NOT NULL REFERENCES users (id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    -- Путь уникален среди используемых документов проекта. Проверка отложена до COMMIT,
    -- чтобы внутри одной сдачи документы могли обменяться путями (переименования).
    CONSTRAINT documents_path_key EXCLUDE USING btree (project_id WITH =, rel_key WITH =)
        WHERE (status = 'active') DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX documents_rel_key_idx ON documents (project_id, rel_key);

CREATE TABLE checkins (
    id      bigserial   PRIMARY KEY,
    user_id uuid        NOT NULL REFERENCES users (id),
    at      timestamptz NOT NULL DEFAULT now(),
    comment text
);

CREATE TABLE versions (
    id                bigserial   PRIMARY KEY,
    document_id       uuid        NOT NULL REFERENCES documents (id),
    number            integer     NOT NULL CHECK (number > 0),
    checkin_id        bigint      NOT NULL REFERENCES checkins (id),
    author_id         uuid        NOT NULL REFERENCES users (id),
    at                timestamptz NOT NULL DEFAULT now(),
    sha256            text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size              bigint      NOT NULL CHECK (size >= 0),
    rel_path          text        NOT NULL,
    cad_version       text,
    cad_version_major integer,
    configurations    jsonb       NOT NULL DEFAULT '[]',
    properties        jsonb       NOT NULL DEFAULT '{}',
    -- Обозначение и наименование, вычисленные из свойств при сдаче (для поиска).
    designation       text,
    name              text,
    CONSTRAINT versions_document_number_key UNIQUE (document_id, number)
);
CREATE INDEX versions_checkin_idx ON versions (checkin_id);
CREATE INDEX versions_author_idx ON versions (author_id);

CREATE TABLE version_refs (
    id                bigserial PRIMARY KEY,
    version_id        bigint    NOT NULL REFERENCES versions (id),
    ref_path          text      NOT NULL,
    child_document_id uuid,
    child_version     integer,
    CONSTRAINT version_refs_child_check CHECK ((child_document_id IS NULL) = (child_version IS NULL)),
    CONSTRAINT version_refs_child_fkey FOREIGN KEY (child_document_id, child_version)
        REFERENCES versions (document_id, number) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX version_refs_version_idx ON version_refs (version_id);
CREATE INDEX version_refs_child_idx ON version_refs (child_document_id, child_version);

CREATE TABLE derived_files (
    version_id bigint NOT NULL REFERENCES versions (id),
    file_name  text   NOT NULL,
    sha256     text   NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size       bigint NOT NULL CHECK (size >= 0),
    PRIMARY KEY (version_id, file_name)
);

CREATE TABLE locks (
    document_id uuid        PRIMARY KEY REFERENCES documents (id),
    user_id     uuid        NOT NULL REFERENCES users (id),
    at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX locks_user_idx ON locks (user_id);

CREATE TABLE notifications (
    id      bigserial   PRIMARY KEY,
    user_id uuid        NOT NULL REFERENCES users (id),
    text    text        NOT NULL,
    at      timestamptz NOT NULL DEFAULT now(),
    seen    boolean     NOT NULL DEFAULT false
);
CREATE INDEX notifications_user_idx ON notifications (user_id, id);

CREATE TABLE journal (
    id          bigserial   PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    user_id     uuid        REFERENCES users (id),
    action      text        NOT NULL,
    document_id uuid,
    version     integer,
    details     jsonb
);
CREATE INDEX journal_document_idx ON journal (document_id, id);

CREATE TABLE settings (
    key   text PRIMARY KEY,
    value text NOT NULL
);

-- Неизменность опубликованного (ТЗ, раздел 9): изменить или удалить нельзя никому, включая владельца схемы.
CREATE FUNCTION pdm_forbid_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Опубликованные данные (таблица %) нельзя изменить или удалить.', TG_TABLE_NAME
        USING ERRCODE = 'P0001', HINT = 'Версии, ссылки, производные файлы, сдачи и журнал только дополняются.';
END
$$;

CREATE TRIGGER versions_immutable BEFORE UPDATE OR DELETE ON versions FOR EACH ROW EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER version_refs_immutable BEFORE UPDATE OR DELETE ON version_refs FOR EACH ROW EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER derived_files_immutable BEFORE UPDATE OR DELETE ON derived_files FOR EACH ROW EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER checkins_immutable BEFORE UPDATE OR DELETE ON checkins FOR EACH ROW EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER journal_immutable BEFORE UPDATE OR DELETE ON journal FOR EACH ROW EXECUTE FUNCTION pdm_forbid_change();

CREATE TRIGGER versions_no_truncate BEFORE TRUNCATE ON versions FOR EACH STATEMENT EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER version_refs_no_truncate BEFORE TRUNCATE ON version_refs FOR EACH STATEMENT EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER derived_files_no_truncate BEFORE TRUNCATE ON derived_files FOR EACH STATEMENT EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER checkins_no_truncate BEFORE TRUNCATE ON checkins FOR EACH STATEMENT EXECUTE FUNCTION pdm_forbid_change();
CREATE TRIGGER journal_no_truncate BEFORE TRUNCATE ON journal FOR EACH STATEMENT EXECUTE FUNCTION pdm_forbid_change();

-- Права роли приложения: опубликованное — только чтение и добавление.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM pdm_app;
GRANT USAGE ON SCHEMA public TO pdm_app;
GRANT SELECT, INSERT ON checkins, versions, version_refs, derived_files, journal TO pdm_app;
GRANT SELECT, INSERT, UPDATE ON users, projects, documents TO pdm_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON sessions, locks, notifications, settings TO pdm_app;
GRANT SELECT ON schema_version TO pdm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pdm_app;
