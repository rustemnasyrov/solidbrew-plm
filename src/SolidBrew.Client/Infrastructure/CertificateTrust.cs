using SolidBrew.Workspace;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Доверие при первом подключении: вопрос пользователю об отпечатке сертификата сервера.</summary>
public static class CertificateTrust
{
    public const string DialogTitle = "Неизвестный сертификат сервера";

    /// <summary>Текст вопроса (для окна и тестов).</summary>
    public static string Question(UntrustedServerCertificateException certificate) =>
        "Сервер предъявил неизвестный сертификат." + Environment.NewLine + Environment.NewLine +
        "Отпечаток SHA-256:" + Environment.NewLine + certificate.DisplayThumbprint + Environment.NewLine + Environment.NewLine +
        "Сверьте отпечаток с администратором PDM. Доверять этому серверу?";

    /// <summary>Спросить, доверять ли сертификату. true — пользователь подтвердил.</summary>
    public static bool Ask(IDialogService dialogs, UntrustedServerCertificateException certificate)
    {
        var c = certificate.Certificate;
        var details = new List<string>();
        if (certificate.Server != null) details.Add("Сервер: " + certificate.Server);
        details.Add("Субъект: " + c.Subject);
        details.Add($"Действителен: с {Format.Time(c.NotBefore)} по {Format.Time(c.NotAfter)}");
        return dialogs.Confirm(DialogTitle, Question(certificate), "Доверять", "Отмена", details);
    }
}
