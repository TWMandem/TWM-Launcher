using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AMM.App;

internal sealed class ServerBrowserWindow : Window
{
    private readonly Action<string> _select;
    private readonly string _selected;
    private readonly (string Name, string Address, string Color)[] _servers =
    {
        ("RED", "80.66.82.192:7777", "#EF4444"),
        ("YELLOW", "80.66.82.193:7777", "#EAB308"),
        ("GREEN", "80.66.82.194:7777", "#22C55E"),
        ("AZURE", "80.66.82.195:7777", "#3B82F6"),
        ("SILVER", "80.66.82.196:7777", "#CBD5E1"),
        ("ROSE", "80.66.71.6:7777", "#A7ADB6"),
        ("BLACK", "80.66.82.40:7777", "#64748B"),
        ("SKY", "80.66.82.41:7777", "#38BDF8"),
        ("TITAN", "80.66.82.165:7777", "#A7ADB6"),
        ("X", "80.66.82.185:7777", "#E2E8F0"),
        ("FIRE", "80.66.82.163:7777", "#F97316"),
        ("LIME", "80.66.82.189:7777", "#A3E635"),
        ("NIGHT", "80.66.82.143:7777", "#A7ADB6")
    };

    public ServerBrowserWindow(string selected, Action<string> select)
    {
        _selected = selected;
        _select = select;
        Title = "Игровые серверы — TWM Launcher";
        Width = 940; Height = 700; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#0A0810"); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "MULTIPLAYER / SERVER BROWSER", Foreground = Brush("#A7ADB6"), FontSize = 10, FontWeight = FontWeights.Bold });
        heading.Children.Add(new TextBlock { Text = "Выбери свой игровой мир", FontSize = 28, FontWeight = FontWeights.ExtraBold, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 5) });
        heading.Children.Add(new TextBlock { Text = "13 игровых миров · выбери сервер и возвращайся в игру", Foreground = Brush("#92929D"), FontSize = 12 });
        root.Children.Add(heading);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = true, Background = Brushes.Transparent };
        scroll.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar))
        {
            Setters = { new Setter(Control.BackgroundProperty, Brush("#111115")), new Setter(Control.ForegroundProperty, Brush("#51515B")), new Setter(FrameworkElement.WidthProperty, 7.0) }
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 4, 0) };
        scroll.Content = cards;

        foreach (var server in _servers)
            {
                bool active = string.Equals(server.Name, _selected, StringComparison.OrdinalIgnoreCase);
                var button = new Button { Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(16), MinHeight = 142, Foreground = Brush("#F4F4F5"), Background = Brush(active ? "#241733" : "#15121B"), BorderBrush = Brush(active ? "#A7ADB6" : "#38264A"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, Tag = server.Name, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null };
                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var top = new DockPanel();
                var dot = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(11), Background = Brush(server.Color), Child = new TextBlock { Text = server.Name.Substring(0, 1), Foreground = Brush("#111111"), FontSize = 15, FontWeight = FontWeights.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                top.Children.Add(dot);
                if (active) { var badge = new TextBlock { Text = "ВЫБРАН", Foreground = Brush("#A7ADB6"), FontSize = 9, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(badge, Dock.Right); top.Children.Add(badge); }
                content.Children.Add(top);
                content.Children.Add(new TextBlock { Text = server.Name, FontSize = 15, FontWeight = FontWeights.ExtraBold, Foreground = Brush("#F4F4F5"), Margin = new Thickness(0, 11, 0, 5) });
                content.Children.Add(new TextBlock { Text = server.Address, Foreground = Brush("#A5A5B0"), FontSize = 11 });
                content.Children.Add(new TextBlock { Text = active ? "Активный мир" : "Нажми, чтобы выбрать", Foreground = Brush(active ? "#A7ADB6" : "#666670"), FontSize = 10, Margin = new Thickness(0, 12, 0, 0) });
                button.Content = content;
                button.Click += (_, _) => { _select(server.Name); DialogResult = true; Close(); };
                button.Template = CardTemplate();
                cards.Children.Add(button);
        }

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(footer, 2);
        footer.Children.Add(new TextBlock { Text = "Выбор сохранится в настройках лаунчера", Foreground = Brush("#777781"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "Закрыть", Padding = new Thickness(20, 11, 20, 11), Background = Brush("#25252C"), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close); root.Children.Add(footer);
        Content = root;
    }

    private static Brush Brush(string color) => new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.White;
    private static ControlTemplate CardTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
