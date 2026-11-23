using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SolidBrew.Client.Views;

public enum MessageKind
{
    Info,
    Question,
    Warning,
    Error,
}

/// <summary>Сообщение, вопрос или выбор варианта с подробностями списком.</summary>
public partial class MessageWindow : Window
{
    private int _result = -1;

    public MessageWindow()
    {
        InitializeComponent();
    }

    /// <summary>Показать окно; возвращает номер нажатой кнопки или -1.</summary>
    public static int Show(Window? owner, string title, string message, IEnumerable<string>? details, MessageKind kind, IReadOnlyList<string> buttons)
    {
        var w = new MessageWindow { Title = title };
        if (owner != null) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.MessageText.Text = message;
        var list = details?.Where(d => !string.IsNullOrWhiteSpace(d)).ToList() ?? new List<string>();
        if (list.Count > 0)
        {
            w.DetailsList.ItemsSource = list;
            w.DetailsScroll.Visibility = Visibility.Visible;
        }
        (w.IconText.Text, w.IconText.Foreground) = kind switch
        {
            MessageKind.Error => ("", (Brush)new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))),
            MessageKind.Warning => ("", new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00))),
            MessageKind.Question => ("", new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xA8))),
            _ => ("", new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xA8))),
        };
        var vertical = buttons.Count > 2 || buttons.Any(b => b.Length > 24);
        if (vertical)
        {
            w.Buttons.Orientation = Orientation.Vertical;
            w.Buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i],
                Padding = new Thickness(14, 4, 14, 4),
                MinWidth = 90,
                Margin = vertical ? new Thickness(0, 0, 0, 6) : new Thickness(8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = i == buttons.Count - 1 && buttons.Count > 1 || buttons.Count == 1,
                HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            };
            b.Click += (_, _) =>
            {
                w._result = index;
                w.Close();
            };
            w.Buttons.Children.Add(b);
        }
        w.ShowDialog();
        return w._result;
    }
}
