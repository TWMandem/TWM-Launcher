using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AMM.App;

/// <summary>A compact in-app confirmation dialog styled to match the Silver palette.</summary>
internal sealed class SilverConfirmWindow : Window
{
    private SilverConfirmWindow(Window? owner, string title, string heading, string message,
        string confirmText, string cancelText, bool warning)
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 540;
        MinWidth = 440;
        MaxWidth = 620;
        Background = new SolidColorBrush(Color.FromRgb(21, 23, 27));
        Foreground = new SolidColorBrush(Color.FromRgb(224, 227, 232));
        FontFamily = new FontFamily("Segoe UI");
        AllowsTransparency = true;

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 27, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 81, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(26)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Child = layout;

        var mark = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromRgb(167, 173, 182)), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(25, 167, 173, 182)),
            Child = new TextBlock { Text = warning ? "!" : "✓", FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(224, 227, 232)), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }
        };
        Grid.SetColumn(mark, 0); Grid.SetRow(mark, 0); layout.Children.Add(mark);

        var copy = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        copy.Children.Add(new TextBlock { Text = "TWM / GAME LAUNCHER", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(167, 173, 182)), Margin = new Thickness(0, 0, 0, 8) });
        copy.Children.Add(new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(242, 243, 245)), Margin = new Thickness(0, 0, 0, 12) });
        copy.Children.Add(new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(192, 197, 204)), LineHeight = 21 });
        Grid.SetColumn(copy, 1); Grid.SetRow(copy, 0); layout.Children.Add(copy);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0) };
        var cancel = MakeButton(cancelText, false);
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = MakeButton(confirmText, true);
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        Grid.SetColumn(buttons, 0); Grid.SetColumnSpan(buttons, 2); Grid.SetRow(buttons, 1); layout.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => cancel.Focus();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false; };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button { Content = text, MinWidth = 104, Height = 40, Padding = new Thickness(14, 0, 14, 0),
            FontSize = 11, FontWeight = FontWeights.Bold, Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = new SolidColorBrush(primary ? Color.FromRgb(196, 202, 210) : Color.FromRgb(26, 29, 33)),
            Foreground = new SolidColorBrush(primary ? Color.FromRgb(17, 20, 25) : Color.FromRgb(211, 215, 221)),
            BorderBrush = new SolidColorBrush(primary ? Color.FromRgb(167, 173, 182) : Color.FromRgb(69, 75, 83)) };
        return button;
    }

    internal static bool Show(Window? owner, string title, string heading, string message,
        string confirmText, string cancelText, bool warning = false)
    {
        var dialog = new SilverConfirmWindow(owner, title, heading, message, confirmText, cancelText, warning);
        return dialog.ShowDialog() == true;
    }
}
