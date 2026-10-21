using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AMM.App;

/// <summary>Silver-themed replacement for standard WPF message boxes.</summary>
internal sealed class SilverMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    private SilverMessageBox(Window? owner, string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Owner = owner;
        Title = caption;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 560;
        MinWidth = 420;
        MaxWidth = 700;
        Background = Brush(21, 23, 27);
        Foreground = Brush(224, 227, 232);
        FontFamily = new FontFamily("Segoe UI");
        AllowsTransparency = true;
        ShowActivated = true;

        var shell = new Border { Background = Brush(25, 27, 31), BorderBrush = Brush(75, 81, 90),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(25) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = grid;

        bool warning = image is MessageBoxImage.Warning or MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop;
        string symbol = image switch
        {
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "×",
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            _ => "i"
        };
        var badge = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
            BorderBrush = Brush((byte)(warning ? 183 : 167), (byte)(warning ? 169 : 173), (byte)(warning ? 158 : 182)),
            BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(25, 167, 173, 182)),
            Child = new TextBlock { Text = symbol, FontSize = 23, FontWeight = FontWeights.Bold,
                Foreground = Brush(224, 227, 232), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center } };
        Grid.SetRow(badge, 0); Grid.SetColumn(badge, 0); grid.Children.Add(badge);

        var content = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        content.Children.Add(new TextBlock { Text = "TWM / GAME LAUNCHER", FontSize = 10,
            Foreground = Brush(167, 173, 182), Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 19, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = Brush(242, 243, 245), Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(192, 197, 204), LineHeight = 21 });
        Grid.SetRow(content, 0); Grid.SetColumn(content, 1); grid.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0) };
        foreach (var item in GetButtons(buttons))
        {
            var button = new Button { Content = item.Text, MinWidth = 94, Height = 40, Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(8, 0, 0, 0), FontSize = 11, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), Background = Brush((byte)(item.Result == defaultResult ? 196 : 26),
                    (byte)(item.Result == defaultResult ? 202 : 29), (byte)(item.Result == defaultResult ? 210 : 33)),
                Foreground = Brush((byte)(item.Result == defaultResult ? 17 : 211), (byte)(item.Result == defaultResult ? 20 : 215),
                    (byte)(item.Result == defaultResult ? 25 : 221)), BorderBrush = Brush(69, 75, 83) };
            var result = item.Result;
            button.Click += (_, _) => { _result = result; Close(); };
            actions.Children.Add(button);
            if (result == defaultResult) Loaded += (_, _) => button.Focus();
        }
        Grid.SetRow(actions, 1); Grid.SetColumn(actions, 0); Grid.SetColumnSpan(actions, 2); grid.Children.Add(actions);
        Content = shell;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = CancelResult(buttons); Close(); } };
        Closed += (_, _) => { if (_result == MessageBoxResult.None) _result = CancelResult(buttons); };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static MessageBoxResult CancelResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK
    };
    private static (string Text, MessageBoxResult Result)[] GetButtons(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => new[] { ("ОТМЕНА", MessageBoxResult.Cancel), ("ОК", MessageBoxResult.OK) },
        MessageBoxButton.YesNo => new[] { ("НЕТ", MessageBoxResult.No), ("ДА", MessageBoxResult.Yes) },
        MessageBoxButton.YesNoCancel => new[] { ("ОТМЕНА", MessageBoxResult.Cancel), ("НЕТ", MessageBoxResult.No), ("ДА", MessageBoxResult.Yes) },
        _ => new[] { ("ОК", MessageBoxResult.OK) }
    };

    internal static MessageBoxResult Show(string message) => Show(null, message, "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.OK);
    internal static MessageBoxResult Show(string message, string caption) => Show(null, message, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.OK);
    internal static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons) => Show(null, message, caption, buttons, MessageBoxImage.None, DefaultFor(buttons));
    internal static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(null, message, caption, buttons, image, DefaultFor(buttons));
    internal static MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(owner, message, caption, buttons, image, DefaultFor(buttons));
    internal static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var dialog = new SilverMessageBox(owner, message, caption, buttons, image, defaultResult);
        dialog.ShowDialog();
        return dialog._result;
    }
    private static MessageBoxResult DefaultFor(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => MessageBoxResult.OK,
        MessageBoxButton.YesNo => MessageBoxResult.Yes,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
        _ => MessageBoxResult.OK
    };
}
