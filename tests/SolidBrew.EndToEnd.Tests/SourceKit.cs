using SolidBrew.Adapters.TestCad;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>
/// Готовый проект на «сетевом диске» (TestCad): замкнутый комплект с подсборкой, общей деталью в двух конфигурациях,
/// двумя исполнениями головной сборки и чертежами головной сборки, подсборки и детали в отдельной папке.
/// <code>
/// Сеть\Изделие\Изделие.tcasm      (Исп1, Исп2) → Сборки\Узел.tcasm, Детали\Корпус.tcprt, Детали\Болт.tcprt
/// Сеть\Изделие\Сборки\Узел.tcasm  → Детали\Вал.tcprt, Детали\Болт.tcprt
/// Сеть\Изделие\Детали\Болт.tcprt  (М8, М10) — общая деталь сборки и подсборки
/// Сеть\Чертежи\Изделие.tcdrw → Изделие\Изделие.tcasm; Узел.tcdrw → подсборка; Корпус.tcdrw → деталь
/// Сеть\Чертежи\Чужой.tcdrw — чертёж другого изделия, не должен попасть в постановку
/// </code>
/// Как в SOLIDWORKS, ссылки записаны <b>абсолютными путями исходной папки</b> (<see cref="Full"/>): сборка из папки изделия
/// ссылается на детали в подпапках, чертежи — на модели из другой папки. Без переписывания ссылок при раскладке тестовый CAD
/// загрузит такие компоненты из исходной папки (тихая подмена) или не найдёт их, если её нет.
/// С проблемами (<see cref="Create"/> с withProblems): головная сборка ссылается на несуществующий «Изделие\Покупные\Двигатель.tcprt»,
/// подсборка — на «D:\Старый проект\Шайба.tcprt», а в папке поиска два файла «Шайба.tcprt» (Детали и Архив).
/// </summary>
public sealed class SourceKit
{
    public const string Head = "Изделие/Изделие.tcasm";
    public const string SubAssembly = "Изделие/Сборки/Узел.tcasm";
    public const string Body = "Изделие/Детали/Корпус.tcprt";
    public const string Shaft = "Изделие/Детали/Вал.tcprt";
    public const string Bolt = "Изделие/Детали/Болт.tcprt";
    public const string Washer = "Изделие/Детали/Шайба.tcprt";
    public const string HeadDrawing = "Чертежи/Изделие.tcdrw";
    public const string SubDrawing = "Чертежи/Узел.tcdrw";
    public const string BodyDrawing = "Чертежи/Корпус.tcdrw";
    /// <summary>Отсутствующий файл, на который ссылается головная сборка комплекта «с проблемами».</summary>
    public const string MissingMotor = "Изделие/Покупные/Двигатель.tcprt";

    public const string HeadDesignation = "АБВ.000";

    private SourceKit(string root) => Root = root;

    /// <summary>Корень «сетевой папки»; относительные пути в PDM считаются от него.</summary>
    public string Root { get; }

    public string HeadFile => Full(Head);
    public string DrawingsFolder => Path.Combine(Root, "Чертежи");

    public string Full(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    public static SourceKit Create(string root, bool withProblems = false)
    {
        var kit = new SourceKit(root);
        void W(string rel, TestCadDocument doc) => TestCadWriter.Write(kit.Full(rel), doc);

        W(Body, new TestCadDocument().Prop("Обозначение", "АБВ.001").Prop("Наименование", "Корпус").Prop("Материал", "Сталь 20").Geom("box 10 20 30"));
        W(Shaft, new TestCadDocument().Prop("Обозначение", "АБВ.002").Prop("Наименование", "Вал").Geom("cyl 5 100"));
        W(Bolt, new TestCadDocument().Config("М8", "М10")
            .Prop("Обозначение", "ГОСТ 7798-70").Prop("Наименование", "Болт")
            .Prop("Обозначение", "Болт М8 ГОСТ 7798-70", "М8").Prop("Обозначение", "Болт М10 ГОСТ 7798-70", "М10")
            .Geom("bolt M8|M10"));

        var sub = new TestCadDocument().Prop("Обозначение", "АБВ.100").Prop("Наименование", "Узел").Ref(kit.Full(Shaft), kit.Full(Bolt));
        var head = new TestCadDocument().Config("Исп1", "Исп2")
            .Prop("Обозначение", HeadDesignation).Prop("Наименование", "Изделие").Prop("Масса", "12,5", "Исп2")
            .Ref(kit.Full(SubAssembly), kit.Full(Body), kit.Full(Bolt));
        if (withProblems)
        {
            head.Ref(kit.Full(MissingMotor));
            sub.Ref(@"D:\Старый проект\Шайба.tcprt");
            W(Washer, new TestCadDocument().Prop("Обозначение", "АБВ.003").Prop("Наименование", "Шайба").Geom("washer 8"));
            W("Изделие/Архив/Шайба.tcprt", new TestCadDocument().Prop("Обозначение", "АБВ.003 (старая)").Geom("washer old"));
        }
        W(SubAssembly, sub);
        W(Head, head);

        W(HeadDrawing, new TestCadDocument().Prop("Обозначение", HeadDesignation + " СБ").Ref(kit.Full(Head)).Geom("sheet A1"));
        W(SubDrawing, new TestCadDocument().Prop("Обозначение", "АБВ.100 СБ").Ref(kit.Full(SubAssembly)).Geom("sheet A2"));
        W(BodyDrawing, new TestCadDocument().Prop("Обозначение", "АБВ.001 Ч").Ref(kit.Full(Body)).Geom("sheet A3"));
        W("Чертежи/Чужой.tcdrw", new TestCadDocument().Prop("Обозначение", "ГДЕ.000").Ref(@"Q:\Другое изделие\Чужая.tcasm"));
        return kit;
    }

    /// <summary>Снимок исходников: хеш, атрибуты, время изменения и создания каждого файла.</summary>
    public Dictionary<string, (string Sha, FileAttributes Attributes, DateTime Written, DateTime Created, long Size)> Snapshot() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(Root, f),
            f => (Computer.Hash(f), File.GetAttributes(f), File.GetLastWriteTimeUtc(f), File.GetCreationTimeUtc(f), new FileInfo(f).Length),
            StringComparer.OrdinalIgnoreCase);
}
