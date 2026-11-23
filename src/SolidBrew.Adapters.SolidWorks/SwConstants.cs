namespace SolidBrew.Adapters.SolidWorks;

/// <summary>Константы API SOLIDWORKS (значения перечислений sw*_e), нужные адаптеру.</summary>
public static class SwConst
{
    // swDocumentTypes_e
    public const int DocNone = 0;
    public const int DocPart = 1;
    public const int DocAssembly = 2;
    public const int DocDrawing = 3;

    // swOpenDocOptions_e
    public const int OpenSilent = 0x1;
    public const int OpenReadOnly = 0x2;
    public const int OpenViewOnly = 0x4;
    public const int OpenRapidDraft = 0x8;
    public const int OpenLoadModel = 0x10;
    public const int OpenAutoMissingConfig = 0x20;
    public const int OpenOverrideDefaultLoadLightweight = 0x40;
    public const int OpenLoadLightweight = 0x80;
    public const int OpenDontLoadHiddenComponents = 0x100;

    // swUserPreferenceStringValue_e
    public const int PrefDefaultTemplatePart = 8;
    public const int PrefDefaultTemplateAssembly = 9;
    public const int PrefDefaultTemplateDrawing = 10;

    // swSaveAsVersion_e / swSaveAsOptions_e
    public const int SaveAsCurrentVersion = 0;
    public const int SaveAsOptionsSilent = 0x1;

    // swCustomInfoType_e / swCustomPropertyAddOption_e
    public const int CustomInfoText = 30;
    public const int CustomPropertyReplaceValue = 2;

    // swEndConditions_e
    public const int EndCondBlind = 0;

    // swSetValueInConfiguration_e
    public const int SetValueInThisConfiguration = 1;
    public const int SetValueInAllConfigurations = 2;

    // swLengthUnit_e
    public const int LengthMm = 0;

    /// <summary>Тип документа SOLIDWORKS по расширению файла (0 — не документ SOLIDWORKS).</summary>
    public static int DocTypeOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".sldprt" => DocPart,
            ".sldasm" => DocAssembly,
            ".slddrw" => DocDrawing,
            _ => DocNone,
        };
    }

    /// <summary>Текст ошибки открытия (swFileLoadError_e) для пользователя.</summary>
    public static string DescribeLoadError(int code)
    {
        var parts = new List<string>();
        void Check(int bit, string text)
        {
            if ((code & bit) != 0) parts.Add(text);
        }
        Check(0x1, "общая ошибка");
        Check(0x2, "файл не найден");
        Check(0x400, "неверный тип файла");
        Check(0x2000, "файл сохранён в более новой версии SOLIDWORKS");
        Check(0x10000, "уже открыт документ с таким же именем");
        Check(0x40000, "не хватает ресурсов");
        Check(0x800000, "SOLIDWORKS занят");
        var text = parts.Count > 0 ? string.Join(", ", parts) : "неизвестная ошибка";
        return $"{text} (код {code})";
    }

    /// <summary>Расшифровка предупреждений открытия (swFileLoadWarning_e) для журнала.</summary>
    public static List<string> DescribeLoadWarnings(int code)
    {
        var names = new (int Bit, string Name)[]
        {
            (0x1, "IdMismatch"), (0x2, "ReadOnly"), (0x4, "SharingViolation"), (0x8, "DrawingANSIUpdate"),
            (0x10, "SheetScaleUpdate"), (0x20, "NeedsRegen"), (0x40, "BasePartNotLoaded"), (0x80, "AlreadyOpen"),
            (0x100, "DrawingsOnlyRapidDraft"), (0x200, "ViewOnlyRestrictions"), (0x400, "ViewMissingReferencedConfig"),
            (0x800, "DrawingSFSymbolConvert"), (0x1000, "RevolveDimTolerance"), (0x2000, "ModelOutOfDate"),
            (0x4000, "DimensionsReferencedIncorrectlyToModels"), (0x8000, "ComponentMissingReferencedConfig"),
            (0x10000, "InvisibleDoc_LinkedDesignTableUpdateFail"), (0x20000, "MissingDesignTable"),
            (0x40000, "AutomaticRepair"),
        };
        var result = new List<string>();
        var rest = code;
        foreach (var (bit, name) in names)
        {
            if ((code & bit) == 0) continue;
            result.Add(name);
            rest &= ~bit;
        }
        if (rest != 0) result.Add($"0x{rest:X}");
        return result;
    }
}
