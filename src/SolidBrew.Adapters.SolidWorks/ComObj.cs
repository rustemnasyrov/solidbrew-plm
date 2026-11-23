using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidBrew.Core.Cad;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>
/// Позднее связывание с объектом COM через <c>IDispatch</c> (<c>Type.InvokeMember</c>).
/// Interop-сборки SOLIDWORKS не нужны. Вызывать только из потока, на котором получен объект (STA адаптера).
/// </summary>
public sealed class ComObj
{
    private const BindingFlags MethodOrGet = BindingFlags.InvokeMethod | BindingFlags.GetProperty;
    private const int RetryCount = 50;

    public ComObj(object raw)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
    }

    /// <summary>Исходный объект COM (RCW).</summary>
    public object Raw { get; }

    /// <summary>Обернуть результат вызова, если это объект COM.</summary>
    public static ComObj? Wrap(object? value) => value == null || value is DBNull ? null : value as ComObj ?? (Marshal.IsComObject(value) ? new ComObj(value) : null);

    /// <summary>Вызов метода или чтение свойства (как в VBA: DISPATCH_METHOD | DISPATCH_PROPERTYGET).</summary>
    public object? Call(string name, params object?[] args) => Invoke(name, MethodOrGet, args, null);

    /// <summary>Вызов, результат которого — объект COM (или null).</summary>
    public ComObj? CallObj(string name, params object?[] args) => Wrap(Call(name, args));

    /// <summary>Чтение свойства, в том числе с параметром (<c>CustomPropertyManager[""]</c>).</summary>
    public object? Get(string name, params object?[] args) => Invoke(name, BindingFlags.GetProperty, args, null);

    public ComObj? GetObj(string name, params object?[] args) => Wrap(Get(name, args));

    /// <summary>Запись свойства.</summary>
    public void Set(string name, object? value) => Invoke(name, BindingFlags.SetProperty, new[] { value }, null);

    /// <summary>
    /// Вызов с параметрами по ссылке (<c>ref</c>/<c>out</c>): после вызова новые значения лежат в <paramref name="args"/>.
    /// </summary>
    public object? CallRef(string name, object?[] args, params int[] refIndexes)
    {
        var modifier = new ParameterModifier(Math.Max(args.Length, 1));
        foreach (var i in refIndexes) modifier[i] = true;
        return Invoke(name, MethodOrGet, args, new[] { modifier });
    }

    public string? CallString(string name, params object?[] args) => AsString(Call(name, args));

    public bool CallBool(string name, params object?[] args) => AsBool(Call(name, args));

    public int CallInt(string name, params object?[] args) => AsInt(Call(name, args));

    private object? Invoke(string name, BindingFlags flags, object?[] args, ParameterModifier[]? modifiers)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Raw.GetType().InvokeMember(name, flags, null, Raw, args, modifiers, CultureInfo.InvariantCulture, null);
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                var hr = inner.HResult;
                if ((hr == ComNative.RPC_E_CALL_REJECTED || hr == ComNative.RPC_E_SERVERCALL_RETRYLATER) && attempt < RetryCount)
                {
                    // SOLIDWORKS занят (модальное окно, пересчёт) — подождать и повторить.
                    Thread.Sleep(200);
                    continue;
                }
                if (ComNative.IsDisconnected(hr))
                    throw new SwDisconnectedException(inner);
                throw new CadException($"Ошибка вызова SOLIDWORKS «{name}»: {inner.Message} (0x{hr:X8}).", inner);
            }
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    // ----- Преобразования результатов -----

    public static string? AsString(object? value) => value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>Логическое значение; объект COM (метод вернул объект вместо bool) считается истиной.</summary>
    public static bool AsBool(object? value) => value != null && value is not DBNull && (Marshal.IsComObject(value) || Convert.ToBoolean(value, CultureInfo.InvariantCulture));

    public static int AsInt(object? value) => value == null || value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>Массив SAFEARRAY строк → список (null → пустой).</summary>
    public static List<string> AsStrings(object? value)
    {
        var result = new List<string>();
        if (value is Array array)
        {
            foreach (var item in array) result.Add(AsString(item) ?? string.Empty);
        }
        else if (value is string s)
        {
            result.Add(s);
        }
        return result;
    }

    /// <summary>Массив объектов COM → список обёрток (null → пустой).</summary>
    public static List<ComObj> AsObjects(object? value)
    {
        var result = new List<ComObj>();
        if (value is Array array)
        {
            foreach (var item in array)
            {
                var obj = Wrap(item);
                if (obj != null) result.Add(obj);
            }
        }
        else
        {
            var obj = Wrap(value);
            if (obj != null) result.Add(obj);
        }
        return result;
    }
}

/// <summary>Связь с SOLIDWORKS потеряна: процесс закрыт или упал.</summary>
public sealed class SwDisconnectedException : CadException
{
    public SwDisconnectedException(Exception inner)
        : base("Связь с SOLIDWORKS потеряна: программа закрыта или перестала отвечать.", inner) { }
}
