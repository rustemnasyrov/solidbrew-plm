using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SolidBrew.Client.Infrastructure;

/// <summary>
/// Значок типа файла, как в Проводнике Windows: берётся у Windows по расширению (для .SLDPRT, .SLDASM, .SLDDRW —
/// значки, которые зарегистрировал установленный SOLIDWORKS; сами файлы значков в программу не входят).
/// Расширение не зарегистрировано — null, показывается глиф программы.
/// </summary>
public static class ShellIcons
{
    private static readonly Dictionary<(string Ext, bool Large), ImageSource?> Cache = new();

    public static ImageSource? For(string? fileName, bool large)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string ext;
        try
        {
            ext = Path.GetExtension(fileName).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (ext.Length < 2) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue((ext, large), out var cached)) return cached;
            var icon = Load(ext, large);
            Cache[(ext, large)] = icon;
            return icon;
        }
    }

    private static ImageSource? Load(string ext, bool large)
    {
        try
        {
            // Без зарегистрированного типа Windows отдаёт значок «неизвестного файла» — тогда лучше свой глиф.
            using (var key = Registry.ClassesRoot.OpenSubKey(ext))
            {
                if (key == null) return null;
            }
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            var ok = SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or COMException or ExternalException)
        {
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// Значок документа: системный значок типа файла (<see cref="FileName"/>), иначе глиф <see cref="Glyph"/> шрифтом значков.
/// Цвет глифа наследуется (Foreground), размер — <see cref="IconSize"/> (≥ 24 — крупный системный значок).
/// </summary>
public sealed class FileIcon : ContentControl
{
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName), typeof(string), typeof(FileIcon), new PropertyMetadata(null, (d, _) => ((FileIcon)d).Update()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(FileIcon), new PropertyMetadata(string.Empty, (d, _) => ((FileIcon)d).Update()));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FileIcon), new PropertyMetadata(16.0, (d, _) => ((FileIcon)d).Update()));

    public FileIcon()
    {
        Focusable = false;
        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    /// <summary>Имя или путь файла (по расширению выбирается значок); null — только глиф.</summary>
    public string? FileName
    {
        get => (string?)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private void Update()
    {
        var size = IconSize;
        var source = ShellIcons.For(FileName, large: size >= 24);
        if (source != null)
        {
            var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Content = image;
            return;
        }
        var text = new TextBlock { Text = Glyph ?? string.Empty, VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("Icon") is Style style) text.Style = style;
        text.FontSize = Math.Max(size - 2, 12);
        Content = text;
    }
}
