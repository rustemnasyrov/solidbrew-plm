using System.IO.Compression;
using System.Text.Json;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Cli;
using SolidBrew.Core.Contracts;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>Командная строка <c>sbpdm</c> против того же тестового сервера: вход, постановка, открытие, сдача, выгрузка.</summary>
[Collection(EndToEndCollection.Name)]
public sealed class CliTests(ServerFixture fx) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed record Run(int Code, string Out, string Err)
    {
        public override string ToString() => $"код {Code}\n--- stdout ---\n{Out}\n--- stderr ---\n{Err}";
    }

    /// <summary>Рабочее место с командной строкой: свой профиль (вход, кэш) и текущая папка.</summary>
    private sealed class Shell(ServerFixture fx, TempDir dir, string name)
    {
        public string Cwd { get; set; } = dir.Root;

        public async Task<Run> RunWithInputAsync(string? stdin, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var env = new CliEnvironment
            {
                Out = output,
                Err = error,
                In = new StringReader(stdin ?? string.Empty),
                CurrentDirectory = Cwd,
                ProfilePath = dir.Path(name, "profile.json"),
                ApiFactory = (_, token, _) => fx.NewClient(token: token),
                ReadSecret = _ => throw new InvalidOperationException("Пароль должен передаваться через --password-stdin."),
            };
            var code = await new CliApp(env).RunAsync(args);
            return new Run(code, output.ToString(), error.ToString());
        }

        public Task<Run> RunAsync(params string[] args) => RunWithInputAsync(null, args);

        public async Task SetupAsync(string login, string password)
        {
            var r = await RunAsync("server", "http://localhost/", "--cache", dir.Path(name, "cache"), "--test-cad", "on");
            Assert.True(r.Code == CliApp.Ok, r.ToString());
            r = await RunWithInputAsync(password + "\n", "login", "--user", login, "--password-stdin");
            Assert.True(r.Code == CliApp.Ok, r.ToString());
            Assert.Contains("Вход выполнен", r.Out);
        }
    }

    private static void AssertCode(int expected, Run r) => Assert.True(r.Code == expected, $"ожидался код {expected}\n{r}");

    /// <summary>ID головного документа из вывода «import --yes».</summary>
    private static string HeadId(Run r) => System.Text.RegularExpressions.Regex.Match(r.Out, @"Головной документ: ID ([0-9a-f-]{36})").Groups[1].Value is { Length: 36 } id ? id : throw new Xunit.Sdk.XunitException("Нет ID головного документа.\n" + r);

    [Fact]
    public async Task Cli_Import_ReportByCategories_ExitCode2_ThenResolvedWithChoices()
    {
        var user = await fx.NewUserAsync("cli");
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"), withProblems: true);
        var sh = new Shell(fx, _dir, "pc1");

        var notLogged = await sh.RunAsync("search", "АБВ");
        AssertCode(CliApp.Failed, notLogged);
        Assert.Contains("Не выполнен вход", notLogged.Err);
        Assert.Single(notLogged.Err.Trim().Split('\n')); // одна понятная строка, без стека

        await sh.SetupAsync(user.Login, ServerFixture.UserPassword);
        var import = new[] { "import", kit.HeadFile, "--project", project.Name, "--extra", kit.DrawingsFolder };

        var report = await sh.RunAsync(import);
        AssertCode(CliApp.NeedsAnswer, report);
        Assert.Contains("Найдены: ", report.Out);
        Assert.Contains("За пределами указанных папок: 0", report.Out);
        Assert.Contains("Только по имени файла: 0", report.Out);
        Assert.Contains("Неоднозначно: 1", report.Out);
        Assert.Contains("Отсутствуют: 1", report.Out);
        Assert.Contains(@"Покупные\Двигатель.tcprt", report.Out);
        Assert.Contains("--choose", report.Out);

        AssertCode(CliApp.Failed, await sh.RunAsync(import.Append("--yes").ToArray()));
        var search = await sh.RunAsync("search", "--project", project.Name, "--json");
        AssertCode(CliApp.Ok, search);
        Assert.Equal(0, JsonDocument.Parse(search.Out).RootElement.GetArrayLength());

        var done = await sh.RunAsync(import.Concat(new[] { "--choose", "Шайба.tcprt=" + kit.Full(SourceKit.Washer), "--allow-missing", "--yes", "--comment", "Из сети" }).ToArray());
        AssertCode(CliApp.Ok, done);
        Assert.Contains("Поставлено на учёт документов: 9", done.Out);
        var head = HeadId(done);

        var found = await sh.RunAsync("search", "--project", project.Name);
        AssertCode(CliApp.Ok, found);
        Assert.Contains(SourceKit.Washer, found.Out);

        var show = await sh.RunAsync("show", head);
        AssertCode(CliApp.Ok, show);
        Assert.Contains("Покупные\\Двигатель.tcprt — отсутствует", show.Out);
        Assert.Contains("Из сети", show.Out);

        var tree = await sh.RunAsync("tree", head);
        AssertCode(CliApp.Ok, tree);
        Assert.Contains("отсутствует (не учтён в PDM)", tree.Out);
        Assert.Contains("  Изделие/Сборки/Узел.tcasm v1", tree.Out);
    }

    [Fact]
    public async Task Cli_Login_Import_Open_Status_Checkin_Export()
    {
        var user = await fx.NewUserAsync("cli");
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"));
        var sh = new Shell(fx, _dir, "pc2");
        await sh.SetupAsync(user.Login, ServerFixture.UserPassword);

        var whoami = await sh.RunAsync("whoami");
        AssertCode(CliApp.Ok, whoami);
        Assert.Contains(user.Login, whoami.Out);

        var imported = await sh.RunAsync("import", kit.HeadFile, "--project", project.Id.ToString(), "--extra", kit.DrawingsFolder, "--yes");
        AssertCode(CliApp.Ok, imported);
        var head = HeadId(imported);

        var ws = _dir.Path("pc2", "Работа");
        var open = await sh.RunAsync("open", head, "--to", ws, "--verify");
        AssertCode(CliApp.Ok, open);
        Assert.Contains("все компоненты загружены из рабочей папки", open.Out);
        Assert.Contains("Конфигурация «Исп2»", open.Out);

        sh.Cwd = Path.Combine(ws, "Изделие");
        var status = await sh.RunAsync("status", "--json");
        AssertCode(CliApp.Ok, status);
        var files = JsonDocument.Parse(status.Out).RootElement.GetProperty("files");
        Assert.Equal(5, files.GetArrayLength());
        Assert.All(files.EnumerateArray(), f => Assert.Equal("free", f.GetProperty("lock").GetString()));

        // Взять, изменить, сдать.
        AssertCode(CliApp.Ok, await sh.RunAsync("checkout", @"Детали\Корпус.tcprt"));
        TestCadWriter.AppendGeometry(Path.Combine(sh.Cwd, "Детали", "Корпус.tcprt"), "chamfer 1");
        var draft = Path.Combine(sh.Cwd, "Детали", "Шпонка.tcprt");
        TestCadWriter.Write(draft, new TestCadDocument().Prop("Обозначение", "АБВ.004").Geom("key 5x5"));

        var plan = await sh.RunAsync("checkin", "--comment", "фаска");
        AssertCode(CliApp.NeedsAnswer, plan);
        Assert.Contains("[x] Изделие/Детали/Корпус.tcprt  (v1 → v2; изменён)", plan.Out);
        Assert.Contains("[x] Изделие/Детали/Шпонка.tcprt  (новый", plan.Out);

        var excluded = await sh.RunAsync("checkin", "--exclude", @"Детали\Шпонка.tcprt", "--comment", "фаска", "--yes");
        AssertCode(CliApp.Ok, excluded);
        Assert.Contains("Сдано документов: 1", excluded.Out);
        Assert.Contains("Изделие/Детали/Корпус.tcprt v2", excluded.Out);

        var after = await sh.RunAsync("status");
        AssertCode(CliApp.Ok, after);
        Assert.Contains("Изделие/Детали/Шпонка.tcprt", after.Out);
        Assert.Contains("черновой", after.Out);

        var used = await sh.RunAsync("where-used", @"Детали\Корпус.tcprt", "--version", "1");
        AssertCode(CliApp.Ok, used);
        Assert.Contains("Изделие/Изделие.tcasm", used.Out);
        Assert.Contains("использует не последнюю версию", used.Out);

        var zip = Path.Combine(_dir.Root, "pc2", "Изделие.zip");
        var export = await sh.RunAsync("export", SourceKit.HeadDesignation, zip, "--version", "1"); // в рабочей папке обозначение ищется в её проекте
        AssertCode(CliApp.Ok, export);
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry(ExportManifest.FileName));
        Assert.NotNull(archive.GetEntry(SourceKit.Body));
        Assert.Contains("файлов: 5", export.Out);

        // Отменить взятие с возвратом правок — только с подтверждением.
        var shaft = Path.Combine(sh.Cwd, "Детали", "Вал.tcprt");
        var shaftSha = Computer.Hash(shaft);
        AssertCode(CliApp.Ok, await sh.RunAsync("checkout", @"Детали\Вал.tcprt"));
        TestCadWriter.AppendGeometry(shaft, "groove");
        AssertCode(CliApp.NeedsAnswer, await sh.RunAsync("undo", @"Детали\Вал.tcprt", "--revert"));
        Assert.NotEqual(shaftSha, Computer.Hash(shaft));
        AssertCode(CliApp.Ok, await sh.RunAsync("undo", @"Детали\Вал.tcprt", "--revert", "--yes"));
        Assert.Equal(shaftSha, Computer.Hash(shaft));
        Assert.True(Computer.IsReadOnly(shaft));

        // Переименование детали подсборки.
        var renamePlan = await sh.RunAsync("rename", @"Детали\Вал.tcprt", @"Детали\Вал-2.tcprt");
        AssertCode(CliApp.NeedsAnswer, renamePlan);
        Assert.Contains("Изделие/Сборки/Узел.tcasm", renamePlan.Out);
        var renamed = await sh.RunAsync("rename", @"Детали\Вал.tcprt", @"Детали\Вал-2.tcprt", "--yes");
        AssertCode(CliApp.Ok, renamed);
        Assert.True(File.Exists(Path.Combine(sh.Cwd, "Детали", "Вал-2.tcprt")));
        var tree = await sh.RunAsync("tree", @"Сборки\Узел.tcasm");
        AssertCode(CliApp.Ok, tree);
        Assert.Contains("Изделие/Детали/Вал-2.tcprt v2", tree.Out);
    }

    [Fact]
    public async Task Cli_Products_OpenWithDrawings_TreeShowsDrawingsUnderModels()
    {
        var user = await fx.NewUserAsync("cli");
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"));
        var sh = new Shell(fx, _dir, "pc4");
        await sh.SetupAsync(user.Login, ServerFixture.UserPassword);
        var imported = await sh.RunAsync("import", kit.HeadFile, "--project", project.Name, "--extra", kit.DrawingsFolder, "--yes");
        AssertCode(CliApp.Ok, imported);
        var head = HeadId(imported);

        var roots = await sh.RunAsync("products", "--project", project.Name);
        AssertCode(CliApp.Ok, roots);
        Assert.Contains(SourceKit.HeadDesignation, roots.Out);
        Assert.DoesNotContain("АБВ.100", roots.Out); // подсборка входит в изделие — не корень
        var all = await sh.RunAsync("products", "--project", project.Name, "--all", "--json");
        AssertCode(CliApp.Ok, all);
        var assemblies = JsonDocument.Parse(all.Out).RootElement.EnumerateArray().ToList();
        Assert.Equal(2, assemblies.Count);
        Assert.Equal(1, assemblies.Single(a => a.GetProperty("designation").GetString() == "АБВ.100").GetProperty("usedInCount").GetInt32());

        var ws = _dir.Path("pc4", "Работа");
        var open = await sh.RunAsync("open", head, "--to", ws, "--with-drawings", "--verify");
        AssertCode(CliApp.Ok, open);
        Assert.Contains("Документов в комплекте: 8", open.Out);
        Assert.True(File.Exists(Path.Combine(ws, "Чертежи", "Корпус.tcdrw")));

        sh.Cwd = ws;
        var tree = await sh.RunAsync("tree");
        AssertCode(CliApp.Ok, tree);
        Assert.Contains("[чертёж] Чертежи/Корпус.tcdrw v1", tree.Out);
        Assert.Contains("    [чертёж] Чертежи/Узел.tcdrw v1", tree.Out); // под подсборкой
        var plain = await sh.RunAsync("tree", "--no-drawings");
        AssertCode(CliApp.Ok, plain);
        Assert.DoesNotContain("[чертёж]", plain.Out);

        var status = await sh.RunAsync("status", "--json");
        AssertCode(CliApp.Ok, status);
        Assert.Equal(8, JsonDocument.Parse(status.Out).RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Cli_Admin_LocksAndSettings_ErrorsAndExitCodes()
    {
        var admin = new Shell(fx, _dir, "admin");
        await admin.SetupAsync("admin", ServerFixture.AdminPassword);
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"));

        var login = fx.Unique("cliuser");
        var add = await admin.RunWithInputAsync(ServerFixture.UserPassword + "\n", "admin", "user-add", login, "Сидоров С. С.", "--role", "designer", "--password-stdin");
        AssertCode(CliApp.Ok, add);
        Assert.Contains("роль — конструктор", add.Out);

        var key = SettingKeys.WorkingVersion("testcad");
        AssertCode(CliApp.Ok, await admin.RunAsync("admin", "settings", key + "=2018"));
        try
        {
            var designer = new Shell(fx, _dir, "designer");
            await designer.SetupAsync(login, ServerFixture.UserPassword);
            var settings = await designer.RunAsync("admin", "settings");
            AssertCode(CliApp.Ok, settings); // конструктор видит рабочие версии CAD
            Assert.Contains(key + " = 2018", settings.Out);
            var denied = await designer.RunAsync("admin", "settings", key + "=2030");
            AssertCode(CliApp.Failed, denied);
            Assert.Contains("только администратору", denied.Err);

            var imported = await designer.RunAsync("import", kit.HeadFile, "--project", project.Name, "--extra", kit.DrawingsFolder, "--yes");
            AssertCode(CliApp.Ok, imported);
            var ws = _dir.Path("designer", "ws");
            AssertCode(CliApp.Ok, await designer.RunAsync("open", HeadId(imported), "--to", ws));
            designer.Cwd = ws;
            AssertCode(CliApp.Ok, await designer.RunAsync("checkout", SourceKit.Bolt));

            // Файл, сохранённый в версии CAD новее рабочей: сдача только с подтверждением.
            var bolt = Path.Combine(ws, SourceKit.Bolt.Replace('/', '\\'));
            File.WriteAllText(bolt, File.ReadAllText(bolt).Replace("cadver: 2018", "cadver: 2024"));
            var newer = await designer.RunAsync("checkin", "--yes");
            AssertCode(CliApp.NeedsAnswer, newer);
            Assert.Contains("новее рабочей версии (2018)", newer.Out);

            // Администратор видит блокировку и снимает её; владелец получает уведомление.
            var locks = await admin.RunAsync("admin", "locks");
            AssertCode(CliApp.Ok, locks);
            Assert.Contains(login, locks.Out);
            AssertCode(CliApp.Ok, await admin.RunAsync("admin", "unlock", bolt)); // документ — по пути файла в рабочей папке
            var status = await designer.RunAsync("status");
            Assert.Contains("сняли принудительно", status.Out);
            var who = await designer.RunAsync("whoami");
            Assert.Contains("снял вашу блокировку", who.Out);

            var blocked = await designer.RunAsync("checkin", "--yes", "--accept-newer-cad");
            AssertCode(CliApp.Ok, blocked); // взятого нет — сдавать нечего, правки сохранены в файле
            Assert.Contains("cadver: 2024", File.ReadAllText(bolt));

            var journal = await admin.RunAsync("admin", "journal", "--limit", "20");
            AssertCode(CliApp.Ok, journal);
            Assert.Contains("блокировка снята администратором", journal.Out);
        }
        finally
        {
            await admin.RunAsync("admin", "settings", key + "=");
        }

        var unknown = await admin.RunAsync("frobnicate");
        AssertCode(CliApp.Failed, unknown);
        Assert.Contains("Неизвестная команда", unknown.Err);
        var badOption = await admin.RunAsync("status", "--bogus");
        AssertCode(CliApp.Failed, badOption);
        var notWorkspace = await admin.RunAsync("status", "--dir", _dir.Path("нет такой"));
        AssertCode(CliApp.Failed, notWorkspace);
        Assert.Contains("не является рабочей папкой", notWorkspace.Err);
        var verbose = await admin.RunAsync("show", Guid.NewGuid().ToString(), "--verbose");
        AssertCode(CliApp.Failed, verbose);
        Assert.Contains("PdmApiException", verbose.Err); // стек — только с --verbose
        var json = await admin.RunAsync("show", Guid.NewGuid().ToString(), "--json");
        Assert.Equal(1, JsonDocument.Parse(json.Out).RootElement.GetProperty("exitCode").GetInt32());

        Assert.Equal(0, await SolidBrew.Cli.Program.Main(new[] { "--version" }));
    }

    /// <summary>
    /// Неизвестный сертификат сервера: «server &lt;адрес&gt;» и «login» печатают отпечаток SHA-256 и выходят с кодом 2;
    /// «server … --trust &lt;отпечаток&gt;» сохраняет отпечаток, только если сервер предъявил именно его.
    /// </summary>
    [Fact]
    public async Task Cli_UnknownServerCertificate_PrintsThumbprint_Code2_ThenTrust()
    {
        var user = await fx.NewUserAsync("tls");
        var serverThumb = new string('5', 64);
        var cert = new SolidBrew.Workspace.ServerCertificateInfo(serverThumb, "CN=D-VPAVLOV", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5),
            System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);
        var used = new List<string?>();
        SolidBrew.Core.Api.IPdmApi Factory(string url, string? token, string? thumbprint)
        {
            used.Add(thumbprint);
            if (thumbprint != serverThumb)
            {
                var untrusted = new SolidBrew.Workspace.UntrustedServerCertificateException(cert, new Uri(url));
                throw new SolidBrew.Core.Api.PdmApiException(0, "Нет связи с сервером PDM: " + untrusted.Message, null, untrusted);
            }
            return fx.NewClient(token: token);
        }

        async Task<Run> Cli(string? stdin, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var env = new CliEnvironment
            {
                Out = output, Err = error, In = new StringReader(stdin ?? string.Empty), CurrentDirectory = _dir.Root,
                ProfilePath = _dir.Path("tls", "profile.json"), ApiFactory = Factory, UseSolidWorks = false,
            };
            return new Run(await new CliApp(env).RunAsync(args), output.ToString(), error.ToString());
        }

        var display = SolidBrew.Workspace.ServerCertificateValidator.FormatThumbprint(serverThumb);
        var first = await Cli(null, "server", "https://10.0.1.9:8443/");
        AssertCode(CliApp.NeedsAnswer, first);
        Assert.Contains(display, first.Err);
        Assert.Contains("sbpdm server https://10.0.1.9:8443/ --trust " + serverThumb, first.Err);

        var login = await Cli(ServerFixture.UserPassword + "\n", "login", "--user", user.Login, "--password-stdin");
        AssertCode(CliApp.NeedsAnswer, login);
        Assert.Contains(display, login.Err);

        var wrong = await Cli(null, "server", "https://10.0.1.9:8443/", "--trust", new string('6', 64));
        AssertCode(CliApp.NeedsAnswer, wrong);
        Assert.Contains("не совпадает", wrong.Err);
        Assert.Contains(display, wrong.Err);

        var trusted = await Cli(null, "server", "https://10.0.1.9:8443/", "--trust", display.ToLowerInvariant());
        AssertCode(CliApp.Ok, trusted);
        Assert.Contains("Доверенный сертификат: SHA-256 " + display, trusted.Out);

        login = await Cli(ServerFixture.UserPassword + "\n", "login", "--user", user.Login, "--password-stdin");
        AssertCode(CliApp.Ok, login);
        Assert.Equal(serverThumb, used[^1]);
    }
}
