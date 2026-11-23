using System.Runtime.InteropServices;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>
/// Подключение к запущенному COM-серверу. В .NET 10 нет <c>Marshal.GetActiveObject</c>,
/// поэтому вызываем <c>CLSIDFromProgID</c> и <c>GetActiveObject</c> напрямую.
/// </summary>
internal static class ComNative
{
    public const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);
    public const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    public const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
    public const int RPC_E_SERVER_DIED = unchecked((int)0x80010007);
    public const int RPC_E_SERVER_DIED_DNE = unchecked((int)0x80010012);
    public const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>Запущенный объект по ProgID или null; <paramref name="hresult"/> — код ошибки.</summary>
    public static object? TryGetActiveObject(string progId, out int hresult)
    {
        hresult = CLSIDFromProgID(progId, out var clsid);
        if (hresult < 0) return null;
        hresult = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        return hresult < 0 ? null : obj;
    }

    /// <summary>Связь с COM-сервером потеряна (процесс закрыт или упал).</summary>
    public static bool IsDisconnected(int hresult) =>
        hresult == RPC_E_DISCONNECTED || hresult == RPC_S_SERVER_UNAVAILABLE || hresult == RPC_E_SERVER_DIED
        || hresult == RPC_E_SERVER_DIED_DNE || hresult == CO_E_OBJNOTCONNECTED;
}
