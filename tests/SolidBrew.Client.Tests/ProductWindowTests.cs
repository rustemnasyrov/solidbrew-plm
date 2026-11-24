using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

/// <summary>Окно изделия: открытие, дерево и фильтр, вкладки, действия, «Сдать (N)», переключатель, «Открыть как изделие».</summary>
public sealed class ProductWindowTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string ProductFolder(string designation) => Path.Combine(_env.Root, "PDM", designation);

    /// <summary>Поставить исходный проект на учёт и открыть его окно изделия в C:\PDM\АБВ.000 (здесь — во временной папке).</summary>
    private async Task<(AppSession Session, ProductWindowViewModel Vm, Guid Head)> OpenProductAsync(string login = "ivanov")
    {
        var s = _env.Session(login);
        var head = await _env.ImportAsync(s);
        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0); // открыть в предложенную папку
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)), _env.Dialogs.AllErrors);
        return (s, vm, head);
    }

    private static ProductNodeViewModel Node(ProductWindowViewModel vm, string title) =>
        vm.Tree.AllNodes().First(n => n.Title == title);

    private static async Task SelectAsync(ProductWindowViewModel vm, ProductNodeViewModel node)
    {
        vm.Tree.Select(node);
        await vm.Panel.LoadSelectedAsync();
    }

    private static async Task RunAsync(ProductWindowViewModel vm, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
    {
        await command.ExecuteAsync(null);
        await vm.PendingRefresh;
    }

    /// <summary>Петров берёт документ в работу в своей папке, правит и сдаёт (или оставляет взятым).</summary>
    private async Task PetrovChangesAsync(Guid head, string relPath, bool checkin)
    {
        var pe = _env.Session("petrov");
        var root = _env.WorkRoot("petrov");
        await _env.OpenDirectAsync(pe, head);
        var id = _env.DocId(relPath);
        var co = await pe.Workspace.CheckOutAsync(root, new[] { id });
        Assert.True(co.Success, co.Message);
        if (!checkin) return;
        TestCadWriter.AppendGeometry(Path.Combine(root, relPath.Replace('/', '\\')), "правка Петрова");
        var plan = await pe.Workspace.PrepareCheckinAsync(root);
        var result = await pe.Workspace.ExecuteCheckinAsync(plan, "правка Петрова");
        Assert.True(result.Success, result.Message);
    }

    // ------------------------------------------------------------------ стартовое окно и открытие

    [Fact]
    public async Task Start_ChooseRootProduct_AsksFolder_OpensWithDrawings_WithoutConflictWindow()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);

        var start = new StartViewModel(s);
        await start.InitializeAsync();
        var item = Assert.Single(start.Catalog.Items);
        Assert.Equal(ProductItem.RootsGroup, item.Group);
        Assert.Equal("АБВ.000", item.Title);
        start.OpenCommand.Execute(item);
        Assert.True(start.DialogResult);
        Assert.Equal(head, start.Choice!.DocumentId);

        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        Assert.True(await vm.OpenProductAsync(start.Choice), _env.Dialogs.AllErrors);

        var folder = ProductFolder("АБВ.000");
        var choice = Assert.Single(_env.Dialogs.Choices);
        Assert.Contains(folder, choice.Message);
        Assert.Empty(_env.Dialogs.Shown); // без конфликтов «Открыть» ничего не спрашивает
        Assert.True(File.Exists(Path.Combine(folder, "Изделие.tcasm")));
        Assert.True(File.Exists(Path.Combine(folder, "Чертежи", "Корпус.tcdrw"))); // открыто с чертежами
        Assert.Equal(folder, vm.Flow.WorkRoot);
        Assert.Equal("АБВ.000", vm.ProductTitle);
        Assert.Equal("Изделие", vm.ProductSubtitle);
        Assert.Equal("v1", vm.ProductVersionText);
        Assert.Equal(head, s.Profile.RecentProducts[0].DocumentId);
        Assert.Equal(folder, s.Profile.RecentProducts[0].WorkRoot);

        // Дерево: чертёж — под моделью, которую изображает.
        Assert.Equal("АБВ.000", vm.Tree.Root!.Title);
        var body = Node(vm, "АБВ.001");
        var drawing = Assert.Single(body.Children);
        Assert.Equal(NodeType.Drawing, drawing.Type);
        Assert.Equal("АБВ.001 СБ", drawing.Title);
        Assert.Same(vm.Tree.Root, vm.Tree.SelectedNode);
        Assert.Equal(StateMark.None, body.Mark); // свободен — без метки
        Assert.False(body.HasState);
        Assert.Equal(0, vm.CheckinCount);
        Assert.False(vm.CheckinAllCommand.CanExecute(null));

        // Второй раз — недавняя папка, ничего не спрашивается и не скачивается.
        var vm2 = new ProductWindowViewModel(s);
        Assert.True(await vm2.OpenProductAsync(new ProductRef(head)));
        Assert.Single(_env.Dialogs.Choices);
        Assert.Equal(folder, vm2.Flow.WorkRoot);
        Assert.Equal(head, ProductFolders.LastProduct(s.Profile)!.DocumentId);
    }

    [Fact]
    public async Task Open_FolderChoiceCancelled_NothingOpened()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(2); // «Отмена»
        Assert.False(await vm.OpenProductAsync(new ProductRef(head)));
        Assert.False(vm.HasProduct);
        Assert.False(Directory.Exists(ProductFolder("АБВ.000")));

        _env.Dialogs.ChooseAnswers.Enqueue(1); // «Выбрать другую папку…»
        _env.Dialogs.FolderToPick = _env.Path("своя папка");
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)), _env.Dialogs.AllErrors);
        Assert.Equal(_env.Path("своя папка"), vm.Flow.WorkRoot);
    }

    [Fact]
    public async Task Open_FolderWithLocalEdits_ShowsConflictWindow()
    {
        var (s, vm, head) = await OpenProductAsync();
        var folder = vm.Flow.WorkRoot!;
        var shaft = Path.Combine(folder, "Детали", "Вал.tcprt");
        File.SetAttributes(shaft, FileAttributes.Normal);
        TestCadWriter.AppendGeometry(shaft, "несданная правка");
        s.Profile.RecentProducts.Clear(); // папка «забыта» — будет «Открыть» в ту же папку

        var vm2 = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        _env.Dialogs.OnShowDialog = d =>
        {
            if (d is OpenViewModel o) o.InitializeAsync().GetAwaiter().GetResult();
            return false;
        };
        Assert.False(await vm2.OpenProductAsync(new ProductRef(head)));
        var open = Assert.IsType<OpenViewModel>(Assert.Single(_env.Dialogs.Shown));
        Assert.True(open.HasConflicts);
    }

    // ------------------------------------------------------------------ дерево и фильтр

    [Fact]
    public async Task Filter_Substring_ShowsMatchesAndAncestors()
    {
        var (_, vm, _) = await OpenProductAsync();
        var root = vm.Tree.Root!;
        var body = Node(vm, "АБВ.001");
        var shaft = Node(vm, "АБВ.002");
        var drawing = Node(vm, "АБВ.001 СБ");

        vm.Tree.FilterText = "вал"; // наименование, без учёта регистра
        Assert.True(shaft.IsVisible && shaft.IsMatch);
        Assert.True(root.IsVisible);
        Assert.False(root.IsMatch);
        Assert.False(body.IsVisible);
        Assert.False(drawing.IsVisible);

        vm.Tree.FilterText = "Корпус.tcdrw"; // имя файла
        Assert.True(drawing.IsVisible && drawing.IsMatch);
        Assert.True(body.IsVisible && body.IsExpanded); // предок совпавшего виден и раскрыт
        Assert.False(body.IsMatch);
        Assert.False(shaft.IsVisible);

        vm.Tree.FilterText = "001"; // обозначение
        Assert.True(body.IsMatch && drawing.IsMatch);
        Assert.False(shaft.IsVisible);

        vm.Tree.FilterText = "нет такого";
        Assert.True(vm.Tree.HasNoMatches);
        Assert.False(root.IsVisible);

        vm.Tree.ClearFilterCommand.Execute(null);
        Assert.All(vm.Tree.AllNodes(), n => Assert.True(n.IsVisible));
        Assert.All(vm.Tree.AllNodes(), n => Assert.False(n.IsMatch));
        Assert.False(vm.Tree.IsFiltered);
    }

    [Fact]
    public async Task QuickFilters_Mine_Modified_Drafts_Outdated_Problems_AndCheckinCount()
    {
        var (_, vm, head) = await OpenProductAsync();
        var folder = vm.Flow.WorkRoot!;

        // Взять «Вал» в работу.
        await SelectAsync(vm, Node(vm, "АБВ.002"));
        await RunAsync(vm, vm.CheckOutCommand);
        Assert.Equal(1, vm.Tree.MineCount);
        var shaft = Node(vm, "АБВ.002");
        Assert.Equal(StateMark.Mine, shaft.Mark);
        Assert.Equal("взят мной", shaft.StateText);
        Assert.Same(shaft, vm.Tree.SelectedNode); // выделение сохранилось после обновления

        vm.Tree.ShowMine = true;
        Assert.True(shaft.IsVisible && shaft.IsMatch);
        Assert.False(Node(vm, "АБВ.001").IsVisible);
        Assert.True(vm.Tree.Root!.IsVisible);
        vm.Tree.ShowMine = false;

        // Правка «Вала» и новый файл → «Сдать (2)».
        TestCadWriter.AppendGeometry(Path.Combine(folder, "Детали", "Вал.tcprt"), "правка");
        TestCadWriter.Write(Path.Combine(folder, "Детали", "Шайба.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.003").Prop("Наименование", "Шайба"));
        await vm.RefreshAsync();
        shaft = Node(vm, "АБВ.002");
        Assert.Equal(StateMark.Modified, shaft.Mark);
        Assert.Equal("изменён", shaft.StateText);
        Assert.Equal(2, vm.CheckinCount);
        Assert.Equal("Сдать (2)", vm.CheckinText);
        Assert.True(vm.CheckinAllCommand.CanExecute(null));
        var drafts = vm.Tree.DraftsGroup!;
        Assert.Equal("Новые файлы", drafts.Title);
        var draft = Assert.Single(drafts.Children);
        Assert.Equal(NodeType.Draft, draft.Type);
        Assert.Equal(StateMark.Draft, draft.Mark);
        Assert.Same(drafts, vm.Tree.Roots.Last()); // внизу дерева

        vm.Tree.ShowModified = true;
        Assert.True(shaft.IsMatch);
        Assert.True(draft.IsMatch && drafts.IsVisible);
        Assert.False(Node(vm, "АБВ.001").IsVisible, string.Join("; ", vm.Tree.AllNodes().Select(n => $"{n.Title}: {n.IsVisible} {n.IsMatch} {n.File?.Local} {n.StateText}")));
        vm.Tree.ShowModified = false;

        // «Сдать (N)» открывает окно сдачи.
        await RunAsync(vm, vm.CheckinAllCommand);
        Assert.IsType<CheckinViewModel>(_env.Dialogs.Shown.Last());

        // Петров сдал новую версию «Корпуса» → у меня он устарел.
        await PetrovChangesAsync(head, "Детали/Корпус.tcprt", checkin: true);
        await vm.RefreshAsync();
        var body = Node(vm, "АБВ.001");
        Assert.True(body.IsOutdated);
        Assert.Equal(StateMark.Outdated, body.Mark);
        Assert.Equal("есть v2", body.StateText);
        Assert.Equal(1, vm.Tree.OutdatedCount);
        vm.Tree.ShowOutdated = true;
        Assert.True(body.IsMatch);
        Assert.False(Node(vm, "АБВ.002").IsVisible);
        vm.Tree.ShowOutdated = false;

        // Проблема: файл чертежа удалён из рабочей папки.
        var drw = Path.Combine(folder, "Чертежи", "Корпус.tcdrw");
        File.SetAttributes(drw, FileAttributes.Normal);
        File.Delete(drw);
        await vm.RefreshAsync();
        var drawing = Node(vm, "АБВ.001 СБ");
        Assert.True(drawing.HasProblem);
        Assert.Equal(StateMark.Missing, drawing.Mark);
        vm.Tree.ShowProblems = true;
        Assert.True(drawing.IsMatch);
        Assert.True(Node(vm, "АБВ.001").IsVisible);
        Assert.False(Node(vm, "АБВ.002").IsVisible);
    }

    // ------------------------------------------------------------------ панель: вкладки

    [Fact]
    public async Task Tabs_DependOnNodeType_AndLastTabIsRememberedPerType()
    {
        var (_, vm, _) = await OpenProductAsync();
        var folder = vm.Flow.WorkRoot!;

        await SelectAsync(vm, vm.Tree.Root!);
        Assert.Equal(new[] { NodeTabIds.Card, NodeTabIds.Composition, NodeTabIds.Versions, NodeTabIds.WhereUsed, NodeTabIds.Journal },
            vm.Panel.Tabs.Select(t => t.Id));
        Assert.Equal("Сборка", vm.Panel.Node!.KindText);
        Assert.Equal("v1 из v1", vm.Panel.Node.VersionText);

        // Карточка: свойства по конфигурациям.
        var card = Assert.IsType<CardTabViewModel>(vm.Panel.SelectedTab!.Content);
        await card.EnsureLoadedAsync();
        Assert.Contains(CardTabViewModel.FileLevelColumn, card.Columns);
        Assert.Contains(card.Properties, p => p.Name == "Обозначение" && p.Values.Contains("АБВ.000"));
        Assert.Contains(card.Info, i => i.Label == "Проект" && i.Value.Contains("Изделие АБВ"));

        // Состав сборки: прямые компоненты.
        Assert.True(vm.Panel.SelectTab(NodeTabIds.Composition));
        var comp = Assert.IsType<CompositionTabViewModel>(vm.Panel.SelectedTab!.Content);
        await comp.EnsureLoadedAsync();
        Assert.Equal(new[] { "АБВ.001", "АБВ.002" }, comp.Components.Select(c => c.Title));
        Assert.All(comp.Components, c => Assert.Equal(1, c.Count));

        // Деталь: без «Состава» и «Модели»; по умолчанию первая вкладка.
        await SelectAsync(vm, Node(vm, "АБВ.002"));
        Assert.Equal(new[] { NodeTabIds.Card, NodeTabIds.Versions, NodeTabIds.WhereUsed, NodeTabIds.Journal }, vm.Panel.Tabs.Select(t => t.Id));
        Assert.Equal(NodeTabIds.Card, vm.Panel.SelectedTab!.Id);
        vm.Panel.SelectTab(NodeTabIds.WhereUsed);
        var used = Assert.IsType<WhereUsedTabViewModel>(vm.Panel.SelectedTab!.Content);
        await used.EnsureLoadedAsync();
        var parent = Assert.Single(used.Items);
        Assert.Equal("АБВ.000", parent.ParentTitle);
        Assert.False(parent.UsesNotLatest);

        // Чертёж: есть «Модель».
        await SelectAsync(vm, Node(vm, "АБВ.001 СБ"));
        Assert.Equal(new[] { NodeTabIds.Card, NodeTabIds.Model, NodeTabIds.Versions, NodeTabIds.WhereUsed, NodeTabIds.Journal }, vm.Panel.Tabs.Select(t => t.Id));
        vm.Panel.SelectTab(NodeTabIds.Model);
        var model = Assert.IsType<ModelTabViewModel>(vm.Panel.SelectedTab!.Content);
        await model.EnsureLoadedAsync();
        var m = Assert.Single(model.Models);
        Assert.Equal("АБВ.001", m.Title);
        Assert.Equal("v1", m.DrawnWithText);
        Assert.False(model.HasMismatch);

        // Возврат к сборке и детали — запомненные вкладки своего типа.
        await SelectAsync(vm, vm.Tree.Root!);
        Assert.Equal(NodeTabIds.Composition, vm.Panel.SelectedTab!.Id);
        await SelectAsync(vm, Node(vm, "АБВ.001"));
        Assert.Equal(NodeTabIds.WhereUsed, vm.Panel.SelectedTab!.Id);
        await SelectAsync(vm, Node(vm, "АБВ.001 СБ"));
        Assert.Equal(NodeTabIds.Model, vm.Panel.SelectedTab!.Id);

        // Черновик — только карточка; служебный узел — без вкладок и действий.
        TestCadWriter.Write(Path.Combine(folder, "Новая.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.009"));
        await vm.RefreshAsync();
        await SelectAsync(vm, vm.Tree.DraftsGroup!.Children[0]);
        Assert.Equal(new[] { NodeTabIds.Card }, vm.Panel.Tabs.Select(t => t.Id));
        await SelectAsync(vm, vm.Tree.DraftsGroup!);
        Assert.Empty(vm.Panel.Tabs);
        Assert.Empty(vm.Panel.PrimaryActions);
    }

    [Fact]
    public async Task Card_HidesEmptyProperties_UntilShowEmptyIsChecked()
    {
        var s = _env.Session("ivanov");
        var file = _env.Path("src3", "Пластина.tcprt");
        TestCadWriter.Write(file, new TestCadDocument().Config("A", "B")
            .Prop("Обозначение", "ПЛ.001").Prop("Наименование", "Пластина")
            .Prop("а_Взам_Инв_№", "").Prop("Лит", " ")
            .Prop("Масса", "", "A").Prop("Масса", "1,2", "B").Prop("Покрытие", "", "A"));
        using (var report = await s.Workspace.PrepareImportAsync(file))
        {
            var result = await s.Workspace.ExecuteImportAsync(report, _env.Project.Id, "Шаблон ГОСТ");
            Assert.True(result.Success, result.Message);
            var vm = new ProductWindowViewModel(s);
            _env.Dialogs.ChooseAnswers.Enqueue(0);
            Assert.True(await vm.OpenProductAsync(new ProductRef(result.HeadDocumentId)), _env.Dialogs.AllErrors);
            await SelectAsync(vm, vm.Tree.Root!);
            var card = Assert.IsType<CardTabViewModel>(vm.Panel.SelectedTab!.Content);
            await card.EnsureLoadedAsync();

            // По умолчанию — только свойства со значением хотя бы в одной конфигурации («Масса» есть в B).
            Assert.Equal(new[] { "Масса", "Наименование", "Обозначение" }, card.Properties.Select(p => p.Name));
            Assert.Equal(3, card.EmptyCount);
            Assert.True(card.HasEmpty);
            Assert.Equal("Показать пустые (3)", card.ShowEmptyText);

            card.ShowEmpty = true;
            Assert.Equal(6, card.Properties.Count);
            Assert.Contains(card.Properties, p => p.Name == "а_Взам_Инв_№" && p.IsEmpty);
            card.ShowEmpty = false;
            Assert.Equal(3, card.Properties.Count);
        }
    }

    [Fact]
    public async Task Registry_AllowsAddingTabWithOneLine()
    {
        var registry = NodeTabRegistry.CreateDefault()
            .Register("spec", "Спецификация", c => c.Type == NodeType.Assembly, c => new CardTabViewModel(c));
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        var vm = new ProductWindowViewModel(s, registry);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)));
        Assert.Equal("spec", vm.Panel.Tabs.Last().Id);
        vm.Tree.Select(Node(vm, "АБВ.002"));
        Assert.DoesNotContain(vm.Panel.Tabs, t => t.Id == "spec");
    }

    // ------------------------------------------------------------------ панель: действия по состоянию и роли

    [Fact]
    public async Task Actions_DependOnStateAndRole()
    {
        var (_, vm, head) = await OpenProductAsync();
        var folder = vm.Flow.WorkRoot!;

        // Свободен.
        await SelectAsync(vm, Node(vm, "АБВ.002"));
        Assert.Equal(new[] { NodeActionKind.OpenInCad, NodeActionKind.CheckOut }, vm.Panel.PrimaryKinds);
        Assert.Equal(new[] { NodeActionKind.Rename, NodeActionKind.WhereUsed, NodeActionKind.MarkUnused, NodeActionKind.ShowInExplorer }, vm.Panel.MoreKinds);
        Assert.Equal(vm.Panel.PrimaryActions.Count + vm.Panel.MoreActions.Count, vm.Panel.MenuActions.Count);

        // Взят мной: «Отменить взятие»; после правки — ещё «Сдать».
        await RunAsync(vm, vm.CheckOutCommand);
        Assert.Equal(new[] { NodeActionKind.OpenInCad, NodeActionKind.UndoCheckOut }, vm.Panel.PrimaryKinds);
        TestCadWriter.AppendGeometry(Path.Combine(folder, "Детали", "Вал.tcprt"), "правка");
        await vm.RefreshAsync();
        Assert.Equal(new[] { NodeActionKind.OpenInCad, NodeActionKind.Checkin, NodeActionKind.UndoCheckOut }, vm.Panel.PrimaryKinds);

        // Взят другим: без «Взять в работу» и «Переименовать».
        await PetrovChangesAsync(head, "Детали/Корпус.tcprt", checkin: false);
        await vm.RefreshAsync();
        var body = Node(vm, "АБВ.001");
        await SelectAsync(vm, body);
        Assert.Equal(StateMark.Other, body.Mark);
        Assert.Equal("взят: Петров П. П.", body.StateText);
        Assert.Contains("Петров П. П.", body.LockText);
        Assert.Equal(new[] { NodeActionKind.OpenInCad }, vm.Panel.PrimaryKinds);
        Assert.DoesNotContain(NodeActionKind.Rename, vm.Panel.MoreKinds);

        // Сборка-корень окна: нет «Открыть как изделие».
        await SelectAsync(vm, vm.Tree.Root!);
        Assert.DoesNotContain(NodeActionKind.OpenAsProduct, vm.Panel.MoreKinds);
        Assert.Contains(NodeActionKind.CheckOut, vm.Panel.PrimaryKinds);
    }

    [Fact]
    public async Task Actions_Outdated_OfferUpdate_AndUpdateWorksWithoutDialog()
    {
        var (_, vm, head) = await OpenProductAsync();
        await PetrovChangesAsync(head, "Детали/Корпус.tcprt", checkin: true);
        await vm.RefreshAsync();
        var body = Node(vm, "АБВ.001");
        await SelectAsync(vm, body);
        Assert.Equal(new[] { NodeActionKind.OpenInCad, NodeActionKind.CheckOut, NodeActionKind.UpdateToLatest }, vm.Panel.PrimaryKinds);

        await RunAsync(vm, vm.UpdateToLatestCommand);
        Assert.Empty(_env.Dialogs.Shown); // правок нет — без окна конфликтов
        body = Node(vm, "АБВ.001");
        Assert.False(body.IsOutdated);
        Assert.Equal(2, body.LocalVersion);
        Assert.DoesNotContain(NodeActionKind.UpdateToLatest, vm.Panel.PrimaryKinds);
    }

    [Fact]
    public async Task Actions_Reader_HasNoModifyingCommands()
    {
        var iv = _env.Session("ivanov");
        var head = await _env.ImportAsync(iv);
        _env.Server.AddUser("reader", "Читатель", UserRole.Reader);
        var s = _env.Session("reader");
        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)), _env.Dialogs.AllErrors);

        await SelectAsync(vm, Node(vm, "АБВ.002"));
        Assert.Equal(new[] { NodeActionKind.OpenInCad }, vm.Panel.PrimaryKinds);
        Assert.Equal(new[] { NodeActionKind.WhereUsed, NodeActionKind.ShowInExplorer }, vm.Panel.MoreKinds);
        Assert.False(vm.CheckinAllCommand.CanExecute(null));
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ переключатель изделий

    [Fact]
    public async Task Switcher_ShowsPinnedRecentAndRoots_SearchFindsAssemblies()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        var other = await ImportSecondProductAsync(s);
        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)));

        // Закрепить текущее изделие.
        Assert.False(vm.IsPinned);
        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);
        Assert.True(s.Profile.IsPinned(head));

        vm.IsSwitcherOpen = true;
        await vm.SwitcherLoading; // загрузка, начатая открытием (вторая не запускается)
        Assert.Same(vm.SwitcherLoading, vm.Switcher.LoadAsync());
        var groups = vm.Switcher.Items.Select(i => (i.Group, i.Title)).ToList();
        Assert.Equal((ProductItem.PinnedGroup, "АБВ.000"), groups[0]);
        Assert.Equal((ProductItem.RecentGroup, "АБВ.000"), groups[1]);
        Assert.Equal(ProductFolder("АБВ.000"), vm.Switcher.Items[1].WorkRoot);
        // Корневые: АБВ.200 (АБВ.000 уже есть выше); подсборка АБВ.100 не корневая.
        Assert.Equal(new[] { "АБВ.200" }, vm.Switcher.Items.Where(i => i.Group == ProductItem.RootsGroup).Select(i => i.Title));

        // Поиск — по всем сборкам, включая не корневые; детали не показываются.
        vm.Switcher.Query = "АБВ.1";
        await vm.Switcher.SearchAsync();
        Assert.Equal(new[] { "АБВ.100" }, vm.Switcher.Items.Select(i => i.Title));
        vm.Switcher.Query = "Вал";
        await vm.Switcher.SearchAsync();
        Assert.Empty(vm.Switcher.Items);
        vm.Switcher.Query = string.Empty;
        await vm.Switcher.SearchAsync();

        // Переключение в том же окне.
        var root2 = vm.Switcher.Items.First(i => i.Title == "АБВ.200");
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        await vm.SwitchToCommand.ExecuteAsync(root2);
        Assert.Equal(other, vm.Product!.DocumentId);
        Assert.Equal(ProductFolder("АБВ.200"), vm.Flow.WorkRoot);
        Assert.False(vm.IsSwitcherOpen);
        Assert.True(Node(vm, "АБВ.100").IsExpanded); // раскрытие нового изделия — по умолчанию, не от прежнего
        Assert.Same(vm.Tree.Root, vm.Tree.SelectedNode);
        vm.Switcher.RebuildLocal();
        Assert.Equal(new[] { "АБВ.200", "АБВ.000" }, vm.Switcher.Recent.Select(r => r.Title));

        // Открепить; «Открыть в новом окне» — событие для приложения.
        vm.Switcher.TogglePinCommand.Execute(vm.Switcher.Pinned.Single());
        Assert.False(s.Profile.IsPinned(head));
        ProductRef? requested = null;
        vm.NewWindowRequested += (_, p) => requested = p;
        vm.OpenItemInNewWindowCommand.Execute(vm.Switcher.Recent.Last());
        Assert.Equal(head, requested!.DocumentId);
        Assert.Equal(ProductFolder("АБВ.000"), requested.WorkRoot);
    }

    /// <summary>Второе изделие: АБВ.200 → подсборка АБВ.100 (→ Вал2) и Корпус2.</summary>
    private async Task<Guid> ImportSecondProductAsync(AppSession s)
    {
        var src = _env.Path("src2");
        string P(string rel) => Path.Combine(src, rel);
        TestCadWriter.Write(P(@"Детали\Вал2.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.102").Prop("Наименование", "Вал 2").Geom("cyl 3 50"));
        TestCadWriter.Write(P(@"Детали\Корпус2.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.201").Prop("Наименование", "Корпус 2").Geom("box 1 2 3"));
        TestCadWriter.Write(P("Узел.tcasm"), new TestCadDocument().Prop("Обозначение", "АБВ.100").Prop("Наименование", "Узел").Ref(@"Детали\Вал2.tcprt"));
        TestCadWriter.Write(P("Изделие2.tcasm"), new TestCadDocument().Prop("Обозначение", "АБВ.200").Prop("Наименование", "Изделие 2")
            .Ref("Узел.tcasm", @"Детали\Корпус2.tcprt"));
        using var report = await s.Workspace.PrepareImportAsync(P("Изделие2.tcasm"));
        var result = await s.Workspace.ExecuteImportAsync(report, _env.Project.Id, "Второе изделие");
        Assert.True(result.Success, result.Message);
        return result.HeadDocumentId;
    }

    // ------------------------------------------------------------------ «Открыть как изделие»

    [Fact]
    public async Task OpenAsProduct_SubAssemblyBecomesRootOfWindow_OrOpensNewWindow()
    {
        var s = _env.Session("ivanov");
        var head = await ImportSecondProductAsync(s);
        var vm = new ProductWindowViewModel(s);
        _env.Dialogs.ChooseAnswers.Enqueue(0);
        Assert.True(await vm.OpenProductAsync(new ProductRef(head)), _env.Dialogs.AllErrors);

        var unit = Node(vm, "АБВ.100");
        await SelectAsync(vm, unit);
        Assert.Equal(NodeType.Assembly, unit.Type);
        Assert.Contains(NodeActionKind.OpenAsProduct, vm.Panel.MoreKinds);
        Assert.Contains(NodeActionKind.OpenAsProductInNewWindow, vm.Panel.MoreKinds);
        Assert.Contains(vm.Panel.MenuActions, a => a.Kind == NodeActionKind.OpenAsProduct); // и в контекстном меню

        ProductRef? requested = null;
        vm.NewWindowRequested += (_, p) => requested = p;
        vm.OpenAsProductInNewWindowCommand.Execute(null);
        Assert.Equal(unit.DocumentId, requested!.DocumentId);

        _env.Dialogs.ChooseAnswers.Enqueue(0);
        await vm.OpenAsProductCommand.ExecuteAsync(null);
        Assert.Equal(unit.DocumentId, vm.Product!.DocumentId);
        Assert.Equal(ProductFolder("АБВ.100"), vm.Flow.WorkRoot); // своя рабочая папка
        Assert.Equal("АБВ.100", vm.Tree.Root!.Title);
        Assert.Equal(new[] { "АБВ.102" }, vm.Tree.Root.Children.Select(c => c.Title));
        Assert.True(File.Exists(Path.Combine(ProductFolder("АБВ.100"), "Узел.tcasm")));
    }

    // ------------------------------------------------------------------ правки в другой рабочей папке

    [Fact]
    public async Task CheckOut_DocumentModifiedInAnotherOfMyFolders_Warns()
    {
        var (s, vm, head) = await OpenProductAsync();
        var shaftId = _env.DocId("Детали/Вал.tcprt");

        // Та же деталь открыта в другой моей рабочей папке, взята и изменена там.
        var other = _env.Path("другая папка");
        var plan = await s.Workspace.PrepareOpenAsync(other, head);
        Assert.True((await s.Workspace.ExecuteOpenAsync(plan)).Success);
        s.Profile.RememberProduct(head, other, "АБВ.000", "Изделие");
        Assert.True((await s.Workspace.CheckOutAsync(other, new[] { shaftId })).Success);
        TestCadWriter.AppendGeometry(Path.Combine(other, "Детали", "Вал.tcprt"), "правка в другой папке");

        await vm.RefreshAsync();
        await SelectAsync(vm, Node(vm, "АБВ.002"));
        Assert.True(Node(vm, "АБВ.002").IsMine); // взят мной — но в другой папке, здесь файл только для чтения
        Assert.Equal(new[] { NodeActionKind.OpenInCad, NodeActionKind.CheckOut, NodeActionKind.UndoCheckOut }, vm.Panel.PrimaryKinds);
        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        await RunAsync(vm, vm.CheckOutCommand);
        var warning = Assert.Single(_env.Dialogs.Confirms);
        Assert.Contains("другой вашей рабочей папке", warning.Message);
        Assert.Contains(warning.Details, d => d.Contains(other) && d.Contains("несданные правки"));
        Assert.True(TestEnv.IsReadOnly(Path.Combine(vm.Flow.WorkRoot!, "Детали", "Вал.tcprt"))); // отказались — не взято здесь

        // «Сдать» тоже предупреждает (здесь правка в обеих папках).
        _env.Dialogs.ConfirmAnswers.Enqueue(true);
        await RunAsync(vm, vm.CheckOutCommand);
        var here = Path.Combine(vm.Flow.WorkRoot!, "Детали", "Вал.tcprt");
        Assert.False(TestEnv.IsReadOnly(here));
        TestCadWriter.AppendGeometry(here, "правка здесь");
        _env.Dialogs.Confirms.Clear();
        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        await vm.CheckinAllCommand.ExecuteAsync(null);
        Assert.Contains("другой вашей рабочей папке", Assert.Single(_env.Dialogs.Confirms).Message);
        Assert.DoesNotContain(_env.Dialogs.Shown, d => d is CheckinViewModel); // отказались — окно сдачи не открывалось
    }
}
