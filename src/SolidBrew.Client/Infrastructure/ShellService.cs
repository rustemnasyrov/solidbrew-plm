using System.Diagnostics;

namespace SolidBrew.Client.Infrastructure;

public sealed class ShellService : IShellService
{
    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"Windows не смогла открыть «{Path.GetFileName(path)}»: {ex.Message} Проверьте, что установлена программа для этого типа файлов (CAD).", ex);
        }
    }

    public void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            throw new IOException($"Не найдено: {path}.");
    }
}
