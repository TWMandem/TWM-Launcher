using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AMM.App;

internal sealed class LocalBuildManagerWindow : Window
{
    private readonly Action<LocalBuildInfo> _activateBuild;
    private readonly Action<LocalBuildInfo> _installSaFix;
    private readonly StackPanel _buildList = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _typeFilter = new();
    private readonly TextBlock _countLabel = new();
    private readonly ProgressBar _importProgress = new();
    private readonly TextBlock _progressDetails = new();
    private readonly Button _importButton;
    private readonly Button _cancelImportButton;
    private CancellationTokenSource? _importCancellation;

    public LocalBuildManagerWindow(Window owner, Action<LocalBuildInfo> activateBuild, Action<LocalBuildInfo> installSaFix)
    {
        Owner = owner;
        _activateBuild = activateBuild;
        _installSaFix = installSaFix;
        Title = "Мои сборки — TWM Launcher";
        Width = 900;
        Height = 700;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(10, 8, 16));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        ResizeMode = ResizeMode.CanResize;

        if (owner.Resources[typeof(ScrollBar)] is Style scrollBarStyle)
            Resources[typeof(ScrollBar)] = scrollBarStyle;

        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "МОИ СБОРКИ",
            FontSize = 26,
            FontWeight = FontWeights.ExtraBold,
            Foreground = Brushes.White
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Полные сборки, САМОФИКСы и DLL-моды для AMAZING ONLINE / CR:MP",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 154, 174)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        header.Children.Add(heading);

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _importButton = MakeButton("＋  Импорт ZIP / RAR", true);
        _importButton.Click += ImportButton_Click;
        headerActions.Children.Add(_importButton);
        _cancelImportButton = MakeButton("Отмена", false);
        _cancelImportButton.Margin = new Thickness(0, 10, 0, 0);
        _cancelImportButton.HorizontalAlignment = HorizontalAlignment.Left;
        _cancelImportButton.MinWidth = 112;
        _cancelImportButton.IsEnabled = false;
        _cancelImportButton.Visibility = Visibility.Collapsed;
        _cancelImportButton.Click += (_, _) =>
        {
            _cancelImportButton.IsEnabled = false;
            _status.Text = "Отмена импорта… завершаем текущую операцию и удаляем временные файлы.";
            _importCancellation?.Cancel();
        };
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        root.Children.Add(header);

        var libraryToolbar = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        libraryToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        libraryToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        libraryToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox.Height = 40;
        _searchBox.Padding = new Thickness(13, 8, 13, 8);
        _searchBox.FontSize = 12;
        _searchBox.Foreground = Brushes.White;
        _searchBox.Background = Brush("#17121F");
        _searchBox.BorderBrush = Brush("#555B64");
        _searchBox.BorderThickness = new Thickness(1);
        _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        _searchBox.ToolTip = "Поиск по названию сборки или имени архива";
        _searchBox.TextChanged += (_, _) => RefreshBuilds();
        Grid.SetColumn(_searchBox, 0);
        libraryToolbar.Children.Add(_searchBox);

        _typeFilter.Height = 40;
        _typeFilter.Margin = new Thickness(10, 0, 0, 0);
        _typeFilter.Padding = new Thickness(8, 4, 8, 4);
        _typeFilter.FontSize = 11;
        _typeFilter.Foreground = Brushes.White;
        _typeFilter.Background = Brush("#17121F");
        _typeFilter.BorderBrush = Brush("#555B64");
        _typeFilter.BorderThickness = new Thickness(1);
        _typeFilter.Items.Add("Все типы");
        _typeFilter.Items.Add("Полные сборки");
        _typeFilter.Items.Add("САМОФИКСы");
        _typeFilter.Items.Add("DLL-моды");
        _typeFilter.SelectedIndex = 0;
        _typeFilter.SelectionChanged += (_, _) => RefreshBuilds();
        Grid.SetColumn(_typeFilter, 1);
        libraryToolbar.Children.Add(_typeFilter);

        _countLabel.Text = "0 СБОРОК";
        _countLabel.VerticalAlignment = VerticalAlignment.Center;
        _countLabel.Margin = new Thickness(14, 0, 0, 0);
        _countLabel.FontSize = 9;
        _countLabel.FontWeight = FontWeights.Bold;
        _countLabel.Foreground = Brush("#A7ADB6");
        Grid.SetColumn(_countLabel, 2);
        libraryToolbar.Children.Add(_countLabel);
        Grid.SetRow(libraryToolbar, 1);
        root.Children.Add(libraryToolbar);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        scroll.Content = _buildList;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _importProgress.Minimum = 0;
        _importProgress.Maximum = 100;
        _importProgress.Height = 8;
        _importProgress.Value = 0;
        _importProgress.Visibility = Visibility.Collapsed;
        _importProgress.Foreground = new SolidColorBrush(Color.FromRgb(181, 108, 255));
        _importProgress.Background = new SolidColorBrush(Color.FromRgb(35, 39, 54));
        footer.Children.Add(_importProgress);
        _progressDetails.Text = "";
        _progressDetails.TextWrapping = TextWrapping.Wrap;
        _progressDetails.FontSize = 10;
        _progressDetails.Foreground = new SolidColorBrush(Color.FromRgb(164, 154, 174));
        _progressDetails.Margin = new Thickness(0, 6, 0, 0);
        _progressDetails.Visibility = Visibility.Collapsed;
        footer.Children.Add(_progressDetails);
        footer.Children.Add(_cancelImportButton);
        _status.Text = "Полные игровые архивы, САМОФИКСы и DLL-паки хранятся отдельно. САМОФИКС устанавливается в выбранную папку игры только после подтверждения.";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 11;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(164, 154, 174));
        _status.Margin = new Thickness(0, 8, 0, 0);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        RefreshBuilds();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери ZIP или RAR с полной сборкой, САМОФИКСом или DLL-модами",
            Filter = "Архивы ZIP и RAR (*.zip;*.rar)|*.zip;*.rar|ZIP-архивы (*.zip)|*.zip|RAR-архивы (*.rar)|*.rar",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        _importCancellation?.Dispose();
        _importCancellation = new CancellationTokenSource();
        CancellationToken token = _importCancellation.Token;
        _importButton.IsEnabled = false;
        _cancelImportButton.IsEnabled = true;
        _cancelImportButton.Visibility = Visibility.Visible;
        _importProgress.Visibility = Visibility.Visible;
        _progressDetails.Visibility = Visibility.Visible;
        _importProgress.Value = 0;
        _progressDetails.Text = "Подготовка импорта…";
        _status.Text = "Импорт выполняется в фоне. Можно отменить операцию; незавершённые файлы будут удалены.";

        var progress = new Progress<LocalBuildImportProgress>(state =>
        {
            _importProgress.Value = Math.Clamp(state.Percent, 0, 100);
            string detail = state.Stage;
            if (state.TotalBytes > 0)
                detail += $" · {FormatSize(state.ProcessedBytes)} / {FormatSize(state.TotalBytes)}";
            if (state.TotalFiles > 0)
                detail += $" · {state.ProcessedFiles:N0}/{state.TotalFiles:N0} файлов";
            if (!string.IsNullOrWhiteSpace(state.CurrentItem))
                detail += Environment.NewLine + state.CurrentItem;
            _progressDetails.Text = detail;
        });

        try
        {
            LocalBuildInfo build = await Task.Run(() => LocalBuildStore.ImportArchive(dialog.FileName, progress, token), token);
            RefreshBuilds();
            _importProgress.Value = 100;
            _status.Text = $"Импортировано: {build.Name} · {build.KindLabel} · {build.FileCount:N0} файлов.";
            _progressDetails.Text = "Импорт завершён успешно.";
            SilverMessageBox.Show(
                $"Архив успешно импортирован.\n\nНазвание: {build.Name}\nТип: {build.KindLabel}\nФайлов: {build.FileCount:N0}\nРазмер распаковки: {FormatSize(build.SizeBytes)}\n\nСодержимое сохранено в C:\\TWM\\Builds\\Local.\nВ игру ничего автоматически не устанавливается.",
                "Импорт завершён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Импорт отменён. Незавершённая сборка удалена; библиотека не изменена.";
            _progressDetails.Text = "Операция отменена.";
        }
        catch (Exception ex)
        {
            _status.Text = "Импорт архива не выполнен.";
            _progressDetails.Text = "Импорт остановлен из-за ошибки.";
            SilverMessageBox.Show("Не удалось импортировать архив. Частично распакованные файлы удалены.\n\n" + ex.Message,
                "Ошибка импорта архива", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _importButton.IsEnabled = true;
            _cancelImportButton.IsEnabled = false;
            _cancelImportButton.Visibility = Visibility.Collapsed;
            _importCancellation?.Dispose();
            _importCancellation = null;
        }
    }

    private void RefreshBuilds()
    {
        _buildList.Children.Clear();
        try
        {
            var allBuilds = LocalBuildStore.GetAll();
            string query = _searchBox.Text?.Trim() ?? string.Empty;
            string type = _typeFilter.SelectedItem as string ?? "Все типы";
            var builds = allBuilds.Where(build =>
            {
                bool matchesQuery = query.Length == 0 ||
                    build.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    build.SourceArchive.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchesType = type switch
                {
                    "Полные сборки" => build.Kind == LocalBuildKind.FullGame,
                    "САМОФИКСы" => build.Kind == LocalBuildKind.SaFix,
                    "DLL-моды" => build.Kind != LocalBuildKind.FullGame && build.Kind != LocalBuildKind.SaFix,
                    _ => true
                };
                return matchesQuery && matchesType;
            }).ToList();

            _countLabel.Text = $"{builds.Count} ИЗ {allBuilds.Count} СБОРОК";
            if (allBuilds.Count == 0)
            {
                AddLibraryMessage("Библиотека пока пуста", "Импортируй ZIP или RAR через кнопку вверху. Архив сначала сохраняется в локальное хранилище TWM Launcher — автоматически в игру ничего не устанавливается.");
                _status.Text = "Полные сборки, САМОФИКСы и DLL-моды хранятся отдельно. Установка выполняется только после подтверждения.";
                return;
            }

            if (builds.Count == 0)
            {
                AddLibraryMessage("Ничего не найдено", "Измени поисковый запрос или выбери другой тип сборки.");
                _status.Text = $"Подходящих сборок нет. Всего в библиотеке: {allBuilds.Count}.";
                return;
            }

            foreach (LocalBuildInfo build in builds)
                _buildList.Children.Add(CreateBuildCard(build));
            _status.Text = $"Показано {builds.Count} из {allBuilds.Count}. Хранилище: TWMBuilds\\local-builds.db";
        }
        catch (Exception ex)
        {
            _countLabel.Text = "ОШИБКА";
            _buildList.Children.Add(new TextBlock
            {
                Text = "Не удалось загрузить библиотеку: " + ex.Message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#FF777F")
            });
        }
    }

    private void AddLibraryMessage(string title, string description)
    {
        var empty = new Border
        {
            Background = Brush("#14101B"),
            BorderBrush = Brush("#3D434B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        content.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#A49AAE"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        empty.Child = content;
        _buildList.Children.Add(empty);
    }

    private UIElement CreateBuildCard(LocalBuildInfo build)
    {
        Color accent = build.Kind switch
        {
            LocalBuildKind.FullGame => Color.FromRgb(181, 108, 255),
            LocalBuildKind.SaFix => Color.FromRgb(255, 190, 105),
            _ => Color.FromRgb(117, 164, 255)
        };
        string category = build.Kind switch
        {
            LocalBuildKind.FullGame => "FULL BUILD",
            LocalBuildKind.SaFix => "GAME FIX",
            _ => "MOD PACK"
        };
        string symbol = build.Kind switch
        {
            LocalBuildKind.FullGame => "TWM",
            LocalBuildKind.SaFix => "FIX",
            _ => "DLL / ASI"
        };

        var card = new Border
        {
            Background = Brush("#100C17"),
            BorderBrush = Brush("#30213D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 12),
            ClipToBounds = true
        };
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stripe = new Border { Background = new SolidColorBrush(accent) };
        Grid.SetColumn(stripe, 0);
        outer.Children.Add(stripe);

        // Artwork-style cover panel built from native WPF elements, so no external assets are required.
        var cover = new Border
        {
            Margin = new Thickness(12, 12, 0, 12),
            CornerRadius = new CornerRadius(9),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(
                Color.FromArgb(235, (byte)Math.Min(255, accent.R / 2 + 18), (byte)Math.Min(255, accent.G / 3 + 10), (byte)Math.Min(255, accent.B / 2 + 25)),
                Color.FromRgb(18, 12, 27), 35)
        };
        var coverGrid = new Grid();
        coverGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        coverGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        coverGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var coverTop = new TextBlock
        {
            Text = "TWM  /  LIBRARY",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Margin = new Thickness(9, 8, 7, 0)
        };
        coverGrid.Children.Add(coverTop);
        var symbolText = new TextBlock
        {
            Text = symbol,
            FontSize = symbol.Length > 5 ? 18 : 27,
            FontWeight = FontWeights.Black,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        Grid.SetRow(symbolText, 1);
        coverGrid.Children.Add(symbolText);
        var categoryText = new TextBlock
        {
            Text = category,
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 0, 4, 8)
        };
        Grid.SetRow(categoryText, 2);
        coverGrid.Children.Add(categoryText);
        cover.Child = coverGrid;
        Grid.SetColumn(cover, 1);
        outer.Children.Add(cover);

        var details = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(15, 13, 12, 13)
        };
        var title = new TextBlock
        {
            Text = build.Name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = build.Name
        };
        details.Children.Add(title);

        var typeBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = build.KindLabel.ToUpper(CultureInfo.CurrentCulture),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent)
            }
        };
        details.Children.Add(typeBadge);

        string date = build.ImportedAtUtc == DateTime.MinValue
            ? "Дата неизвестна"
            : build.ImportedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        var metadata = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        AddMetadataChip(metadata, $"▦  {build.FileCount:N0} файлов");
        AddMetadataChip(metadata, $"◈  {FormatSize(build.SizeBytes)}");
        AddMetadataChip(metadata, $"◷  {date}");
        details.Children.Add(metadata);
        details.Children.Add(new TextBlock
        {
            Text = "АРХИВ  ·  " + build.SourceArchive,
            FontSize = 9,
            Foreground = Brush("#82758F"),
            Margin = new Thickness(0, 8, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = build.SourceArchive
        });
        Grid.SetColumn(details, 2);
        outer.Children.Add(details);

        var actions = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 12, 14, 12)
        };
        var open = MakeButton("Открыть папку", false);
        open.HorizontalContentAlignment = HorizontalAlignment.Center;
        open.MinWidth = 132;
        open.Margin = new Thickness(0, 0, 0, 6);
        open.Click += (_, _) => OpenBuildFolder(build);
        actions.Children.Add(open);

        if (build.Kind == LocalBuildKind.FullGame)
        {
            var activate = MakeButton("▶  Использовать", true);
            activate.MinWidth = 132;
            activate.Click += (_, _) => _activateBuild(build);
            actions.Children.Add(activate);
        }
        else if (build.Kind == LocalBuildKind.SaFix)
        {
            var install = MakeButton("Установить в игру", true);
            install.MinWidth = 132;
            install.Margin = new Thickness(0, 0, 0, 6);
            install.Click += (_, _) => _installSaFix(build);
            actions.Children.Add(install);

            var restore = MakeButton("↶  Восстановить", false);
            restore.MinWidth = 132;
            restore.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 105));
            restore.Margin = new Thickness(0, 0, 0, 6);
            restore.Click += (_, _) => RestoreSaFix(build);
            actions.Children.Add(restore);
        }

        var delete = MakeButton("Удалить сборку", false);
        delete.MinWidth = 132;
        delete.Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 153));
        delete.Click += (_, _) => DeleteBuild(build);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 3);
        outer.Children.Add(actions);

        card.Child = outer;
        return card;
    }

    private static void AddMetadataChip(Panel parent, string text)
    {
        parent.Children.Add(new Border
        {
            Background = Brush("#1A1423"),
            BorderBrush = Brush("#30213D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = Brush("#C7BDCF")
            }
        });
    }

    private void OpenBuildFolder(LocalBuildInfo build)
    {
        try
        {
            string path = LocalBuildStore.GetContentDirectory(build);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SilverMessageBox.Show("Не удалось открыть папку сборки.\n\n" + ex.Message,
                "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreSaFix(LocalBuildInfo build)
    {
        string gameDirectory;
        try
        {
            // Путь берётся из manifest.json, созданного при установке САМОФИКСа.
            gameDirectory = LocalBuildStore.GetRestorableSaFixTargetDirectory(build);
        }
        catch (Exception ex)
        {
            SilverMessageBox.Show(ex.Message, "Возврат стандартных файлов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBoxResult answer = SilverMessageBox.Show(
            $"Вернуть стандартные файлы в папке, сохранённой при установке САМОФИКСа?\n\n{gameDirectory}\n\n" +
            "Лаунчер восстановит оригиналы из резервной копии и удалит файлы, добавленные САМОФИКСом. " +
            "Резервная копия останется на диске.",
            "Возврат стандартных файлов", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var result = LocalBuildStore.RestoreLatestSaFix(build);
            _status.Text = $"Стандартные файлы восстановлены. Возвращено: {result.RestoredFiles:N0}; удалено добавленных файлов: {result.RemovedFiles:N0}.";
            SilverMessageBox.Show(
                $"Восстановление завершено.\n\nПапка игры:\n{gameDirectory}\n\nВосстановлено оригинальных файлов: {result.RestoredFiles:N0}.\nУдалено файлов, добавленных САМОФИКСом: {result.RemovedFiles:N0}.\n\nРезервная копия сохранена:\n{result.BackupDirectory}",
                "Стандартные файлы восстановлены", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SilverMessageBox.Show("Не удалось восстановить стандартные файлы.\n\n" + ex.Message,
                "Ошибка восстановления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteBuild(LocalBuildInfo build)
    {
        MessageBoxResult answer = SilverMessageBox.Show(
            $"Удалить «{build.Name}» и все файлы этой импортированной сборки?\n\nУдаление необратимо. Файлы за пределами хранилища TWM Launcher не затрагиваются.",
            "Удаление сборки", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            LocalBuildStore.Delete(build);
            RefreshBuilds();
            _status.Text = $"Сборка «{build.Name}» удалена.";
        }
        catch (Exception ex)
        {
            SilverMessageBox.Show("Не удалось удалить сборку.\n\n" + ex.Message,
                "TWM Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static Button MakeButton(string text, bool accent)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(12, 9, 12, 9),
            Background = new SolidColorBrush(accent ? Color.FromRgb(181, 108, 255) : Color.FromRgb(28, 20, 38)),
            Foreground = new SolidColorBrush(accent ? Color.FromRgb(22, 13, 32) : Color.FromRgb(235, 237, 245)),
            BorderBrush = new SolidColorBrush(accent ? Color.FromRgb(181, 108, 255) : Color.FromRgb(68, 49, 79)),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
