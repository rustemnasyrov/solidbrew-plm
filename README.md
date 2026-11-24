# SolidBrew PDM

Хранилище конструкторских документов SOLIDWORKS: документы, версии, ссылки сборок и чертежей, взятие в работу, сдача, рабочие папки на компьютерах конструкторов.

[Целевая модель](docs/pdm-concept.md) описывает общее хранилище для SOLIDWORKS, КОМПАС-3D и KiCad, [ТЗ](docs/pdm-spec.md) задаёт этапы и критерии приёмки, [архитектура](docs/architecture.md) — стек и устройство компонентов.

## Что в репозитории

- `src/SolidBrew.Api/` — сервер (ASP.NET Core в Docker): документы, версии, блокировки, вход по учётным записям, HTTPS.
- `src/SolidBrew.Client/` — клиент для Windows (`SolidBrew.exe`); `src/SolidBrew.Cli/` — командная строка `sbpdm.exe`.
- `src/SolidBrew.Workspace/`, `src/SolidBrew.Adapters.*` — рабочие папки, кэш, адаптеры SOLIDWORKS и тестового CAD.
- `compose.yaml` — PostgreSQL и API. Архив содержимого — папка на диске сервера вне Docker-томов (`PDM_ARCHIVE_DIR`). S3-вариант — `infra/compose.s3.yaml`, по умолчанию не используется.
- `tools/` — подготовка сервера (`New-LocalEnv.ps1`, `New-ServerCert.ps1`; для S3-варианта — `Install-MinIO.ps1`, `Initialize-MinIO.ps1`), публикация клиента (`Publish-Client.ps1`), запуск заданий на машине с SOLIDWORKS (`Start-SwProbe.ps1`, `Invoke-SwJob.ps1`, `Invoke-InUserSession.ps1`).

## Устройство сервера

| Компонент | Где работает | Адрес |
|---|---|---|
| API | Docker Compose, контейнер `api` | `https://10.0.1.9:8443/` — для рабочих мест (самоподписанный сертификат); `http://127.0.0.1:8088/` — только диагностика с самого сервера |
| PostgreSQL | Docker Compose, том `postgres-data` | только внутренняя сеть Compose, портов наружу нет |
| Архив файлов | Папка на диске сервера `PDM_ARCHIVE_DIR`, подключена в контейнер API как `/archive` | наружу не публикуется; раскладка `objects/<2 символа>/<sha256>`, файлы только дописываются |

MinIO не используется: с 2025 года community-сборки MinIO для Windows не публикуются, `dl.min.io` отвечает 410. Код S3-хранилища оставлен опцией (`infra/compose.s3.yaml`, `tools/Install-MinIO.ps1`).

Все секреты — в файле `.env` в папке репозитория (в git не попадает) и в `C:\SolidBrew\certs\` (сертификат).

## Установка сервера (Windows, Docker Desktop)

Нужен Docker Desktop с Linux-контейнерами; права администратора не нужны. Все команды — PowerShell из папки репозитория.

1. **Файл `.env`** (один раз; существующие значения не перезаписываются, недостающие ключи дописываются):

   ```powershell
   .\tools\New-LocalEnv.ps1 -BindAddress 10.0.1.9
   ```

   `PDM_BIND_ADDRESS` — адрес сервера в ЛВС, на котором API принимает HTTPS; `PDM_ARCHIVE_DIR` — папка архива файлов (вне репозитория, на диске с запасом места); `PDM_BOOTSTRAP_ADMIN_PASSWORD` — пароль первого администратора.

2. **Сертификат HTTPS** (без прав администратора):

   ```powershell
   .\tools\New-ServerCert.ps1
   ```

   Создаёт самоподписанный сертификат (RSA 3072, 5 лет, имена `D-VPAVLOV`, `localhost`, `10.0.1.9`, `127.0.0.1`) в `C:\SolidBrew\certs\pdm-api.pfx`, дописывает в `.env` `PDM_TLS_PFX_PATH` и `PDM_TLS_PFX_PASSWORD` и печатает **отпечаток SHA-256**. Запишите его: конструкторы сверяют с ним отпечаток при первом подключении. Повторный запуск только печатает отпечаток; `-Force` выпускает новый сертификат (клиенты спросят о доверии заново).

3. **Брандмауэр.** На D-VPAVLOV порт HTTPS API (8443) уже доступен из ЛВС — его открывает правило Docker Desktop, отдельное правило не нужно. Ограничить доступ подсетью можно только вместе с правилами Docker Desktop; это решение администратора.

4. **PostgreSQL и API**:

   ```powershell
   docker compose up -d --build
   docker compose logs api        # в журнале — «HTTPS: порт 8443, сертификат SHA-256 …»
   Invoke-RestMethod http://127.0.0.1:8088/healthz
   ```

   Compose перезапускает контейнеры сам (`restart: unless-stopped`), но Docker Desktop запускается при входе пользователя в Windows: после перезагрузки сервера на нём должен войти пользователь, от имени которого работает Docker Desktop. `docker compose down` не удаляет данные; `docker compose down -v` уничтожает базу.

5. **Первый вход** — пользователь `admin`, пароль — значение `PDM_BOOTSTRAP_ADMIN_PASSWORD` в `.env`. Смените его и заведите конструкторов (в клиенте — раздел «Администрирование», или командной строкой):

   ```powershell
   .\publish\client\sbpdm.exe admin user-set admin --password-stdin
   .\publish\client\sbpdm.exe admin user-add ivanov "Иванов И. И." --role designer
   ```


## Рабочее место конструктора

1. Публикация клиента (на машине разработки):

   ```powershell
   .\tools\Publish-Client.ps1
   ```

   Результат — папка `publish\client\` с `SolidBrew.exe` и `sbpdm.exe`. Сборка самодостаточная (self-contained, win-x64): .NET на рабочем месте не нужен. Папку копируют на рабочее место или запускают из общей папки.

2. Запуск `SolidBrew.exe`. В окне входа (или «Настройки…») укажите сервер **`https://10.0.1.9:8443/`**, логин и пароль.

3. При первом подключении клиент покажет: «Сервер предъявил неизвестный сертификат. Отпечаток: …». **Сверьте отпечаток с администратором** (он есть в выводе `New-ServerCert.ps1` и в журнале API) и нажмите «Доверять». Отпечаток сохраняется в профиле пользователя (`%APPDATA%\SolidBrew\profile.json`); следующие подключения проходят без вопроса. Если сертификат на сервере заменят, клиент снова спросит; сбросить доверие — «Настройки» → «Забыть».

Командная строка — то же самое:

```powershell
sbpdm server https://10.0.1.9:8443/                      # неизвестный сертификат: печатает отпечаток, код 2
sbpdm server https://10.0.1.9:8443/ --trust <отпечаток>  # доверять (только если сервер предъявил именно его)
sbpdm login --user ivanov
```

## Резервное копирование

Архив только пополняется: объекты сохраняются по SHA-256 до записи версии в БД и API их не удаляет. Поэтому согласованная копия получается так: **сначала база, потом папка архива** — все объекты, на которые ссылается копия базы, уже лежат в архиве.

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$dest = "D:\Backup\SolidBrew\$stamp"
New-Item -ItemType Directory -Force $dest | Out-Null
# 1. База: дамп внутри контейнера и копия наружу (не через перенаправление PowerShell — оно портит двоичный вывод).
docker compose exec -T postgres pg_dump -U pdm -d pdm -Fc -f /tmp/pdm.dump
docker compose cp postgres:/tmp/pdm.dump "$dest\pdm.dump"
docker compose exec -T postgres rm /tmp/pdm.dump
# 2. Папка архива.
robocopy "<PDM_ARCHIVE_DIR>" "$dest\archive" /E /COPY:DAT /R:1 /W:1
# 3. Секреты и сертификат — отдельно, в защищённое место: .env, C:\SolidBrew\certs\pdm-api.pfx.
```

Папка архива — обычные файлы; восстанавливается копированием в `PDM_ARCHIVE_DIR`. Восстановление базы на новом сервере: `docker compose up -d` (API создаст схему и роли), `docker compose stop api`, `docker compose cp pdm.dump postgres:/tmp/pdm.dump`, `docker compose exec -T postgres pg_restore -U pdm -d pdm --clean --if-exists /tmp/pdm.dump`, `docker compose start api`. Процедуру восстановления проверьте на отдельной машине до того, как она понадобится.

## Разработка

```powershell
.\.tools\dotnet\dotnet.exe build SolidBrew.slnx
.\.tools\dotnet\dotnet.exe test SolidBrew.slnx
```

SOLIDWORKS для проверки адаптера установлен на отдельном компьютере; как гонять там задания — [architecture.md, раздел 5](docs/architecture.md#где-и-как-проверяется-всё-что-связано-с-solidworks).
