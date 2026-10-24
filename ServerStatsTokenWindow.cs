using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AMM.App;

/// <summary>
/// One-time local token entry. The token is never shown in a textbox or written to a plain file.
/// </summary>
internal sealed class ServerStatsTokenWindow : Window
{
    private readonly PasswordBox _tokenBox;

    public string Token => _tokenBox.Password.Trim();

    public ServerStatsTokenWindow()
    {
        Title = "Подключение статистики серверов";
        Width = 570;
        Height = 310;
        MinWidth = 570;
        MaxWidth = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 238));
        FontFamily = new FontFamily("Segoe UI");

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(21, 23, 28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 81, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(24)
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = layout;
        Content = shell;

        var title = new TextBlock
        {
            Text = "Подключение статистики Amazing Online",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        layout.Children.Add(title);

        var instructions = new TextBlock
        {
            Text = "Открой официальный Amazing Launcher и скопируй значение параметра token из свежего запроса servers.stats в HTTP Toolkit. Вставь только сам токен — без URL и без «token=».",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(176, 181, 190)),
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(instructions, 1);
        layout.Children.Add(instructions);

        _tokenBox = new PasswordBox
        {
            Height = 38,
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(12, 14, 18)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 81, 90)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(_tokenBox, 2);
        layout.Children.Add(_tokenBox);

        var note = new TextBlock
        {
            Text = "Токен не отправляется в чат и не сохраняется открытым текстом. После успешной проверки он будет зашифрован Windows DPAPI для текущего пользователя.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 151, 161)),
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(note, 3);
        layout.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancel = new Button
        {
            Content = "Отмена",
            Width = 100,
            Height = 36,
            Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(Color.FromRgb(48, 52, 59)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            IsCancel = true
        };
        cancel.Click += (_, _) => DialogResult = false;

        var save = new Button
        {
            Content = "Проверить и сохранить",
            Width = 175,
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(145, 153, 163)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            IsDefault = true
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_tokenBox.Password))
            {
                MessageBox.Show(this, "Вставь значение токена.", "Токен не указан",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _tokenBox.Focus();
                return;
            }

            DialogResult = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);

        Loaded += (_, _) => _tokenBox.Focus();
    }
}
