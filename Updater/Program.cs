using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TWM.Launcher.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new UpdateWindow(args);
        app.Run(window);
    }
}

internal sealed class PackageManifest
{
    public string PackageVersion { get; set; } = "";
    public List<PackageFile> Files { get; set; } = [];
}

internal sealed class PackageFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class UpdateWindow : Window
{
    private readonly string[] _args;
    private readonly TextBlock _heading;
    private readonly TextBlock _detail;
    private readonly TextBlock _percentText;
    private readonly ProgressBar _progress;
    private readonly Button _closeButton;
    private bool _busy = true;
    private bool _allowClose;

    public UpdateWindow(string[] args)
    {
        _args = args;
        Title = "TWM Launcher — Обновление";
        Width = 540;
        Height = 250;
        MinWidth = Width;
        MinHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(17, 20, 27));
        Foreground = new SolidColorBrush(Color.FromRgb(235, 239, 247));
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = true;

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var brand = new TextBlock
        {
            Text = "TWM  /  LAUNCHER UPDATE",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(119, 160, 255)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(brand);

        _heading = new TextBlock
        {
            Text = "Подготавливаем обновление",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_heading, 1);
        root.Children.Add(_heading);

        var progressPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _detail = new TextBlock
        {
            Text = "Проверяем пакет перед установкой…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(165, 175, 193)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 12)
        };
        progressPanel.Children.Add(_detail);
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 7,
            IsIndeterminate = true,
            Background = new SolidColorBrush(Color.FromRgb(42, 48, 61)),
            Foreground = new SolidColorBrush(Color.FromRgb(102, 145, 255))
        };
        progressPanel.Children.Add(_progress);
        _percentText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(165, 175, 193)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 7, 0, 0)
        };
        progressPanel.Children.Add(_percentText);
        Grid.SetRow(progressPanel, 2);
        root.Children.Add(progressPanel);

        _closeButton = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromRgb(49, 59, 78)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _closeButton.Click += (_, _) => { _allowClose = true; Close(); };
        Grid.SetRow(_closeButton, 3);
        root.Children.Add(_closeButton);

        Content = root;
        Closing += (_, e) => { if (_busy && !_allowClose) e.Cancel = true; };
        Loaded += async (_, _) => await RunUpdateAsync();
    }

    private async Task RunUpdateAsync()
    {
        string? stageDirectory = null;
        string? backupDirectory = null;
        string? packagePath = null;
        string? installDirectory = null;
        string? launcherPath = null;
        bool launcherStopped = false;
        var changed = new List<(string Relative, bool HadOriginal)>();
        var originalFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_args.Length != 4)
                throw new ArgumentException("Неверные параметры запуска обновлятора.");

            packagePath = Path.GetFullPath(_args[0]);
            installDirectory = Path.GetFullPath(_args[1]);
            if (!int.TryParse(_args[2], out var launcherPid) || launcherPid <= 0)
                throw new ArgumentException("Не удалось определить процесс лаунчера.");
            launcherPath = Path.GetFullPath(_args[3]);

            if (!File.Exists(packagePath))
                throw new FileNotFoundException("Не найден скачанный ZIP-пакет.", packagePath);
            if (!File.Exists(launcherPath))
                throw new FileNotFoundException("Не найден исполняемый файл лаунчера.", launcherPath);

            var updatesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TWM Launcher", "Updates");
            Directory.CreateDirectory(updatesRoot);
            var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            stageDirectory = Path.Combine(updatesRoot, "stage-" + runId);
            backupDirectory = Path.Combine(updatesRoot, "rollback-" + runId);
            Directory.CreateDirectory(stageDirectory);
            Directory.CreateDirectory(backupDirectory);

            SetStatus("Проверяем файлы обновления", "Проверяем размеры и SHA-256 каждого файла пакета…", 0, true);
            var files = await StageAndVerifyPackageAsync(packagePath, stageDirectory);

            SetStatus("Ожидаем закрытия лаунчера", "Пакет проверен. Завершаем старую версию перед заменой файлов…", 60, true);
            await WaitForLauncherExitAsync(launcherPid);
            launcherStopped = true;

            SetStatus("Создаём резервную копию", "Сохраняем текущие файлы, которые будут заменены…", 62, false);
            for (var i = 0; i < files.Count; i++)
            {
                var relative = files[i].Path;
                var target = SafeCombine(installDirectory, relative);
                var backup = SafeCombine(backupDirectory, relative);
                var hadOriginal = File.Exists(target);
                originalFiles[relative] = hadOriginal;

                if (Directory.Exists(target))
                    throw new IOException($"Вместо файла обнаружена папка: {relative}");

                if (hadOriginal)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: false);
                }

                if (files.Count > 0)
                    SetStatus("Создаём резервную копию", relative, 62 + (int)((i + 1) * 8d / files.Count), false);
            }

            SetStatus("Устанавливаем обновление", "Заменяем проверенные файлы приложения…", 70, false);
            for (var i = 0; i < files.Count; i++)
            {
                var relative = files[i].Path;
                var source = SafeCombine(stageDirectory, relative);
                var target = SafeCombine(installDirectory, relative);
                var targetDirectory = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetDirectory);

                var temporaryTarget = target + ".twm-update-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    // Сначала регистрируем файл для возможного отката.
                    changed.Add((relative, originalFiles[relative]));

                    // Создаём временный файл и заменяем целевой.
                    File.Copy(source, temporaryTarget, overwrite: false);
                    File.Move(temporaryTarget, target, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget); } catch { }
                }

                SetStatus("Устанавливаем обновление", relative,
                    70 + (int)((i + 1) * 25d / files.Count), false);
            }

            SetStatus("Запускаем TWM Launcher", "Обновление установлено. Запускаем новую версию…", 100, false);
            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = installDirectory,
                UseShellExecute = true
            });

            // Запускаем отдельный процесс очистки.
            // Он дождётся завершения обновлятора и удалит временные файлы.
            ScheduleCleanupAfterExit(updatesRoot, Environment.ProcessId);

            // Резервную копию rollback-* сохраняем для возможного восстановления.
            _busy = false;
            await Task.Delay(700);
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            string rollbackMessage = "";
            if (installDirectory is not null && backupDirectory is not null && changed.Count > 0)
            {
                try
                {
                    SetStatus("Восстанавливаем предыдущую версию", "Во время установки возникла ошибка. Откатываем изменения…", 0, true);
                    await RollBackAsync(installDirectory, backupDirectory, changed);
                    rollbackMessage = " Предыдущая версия восстановлена.";
                }
                catch (Exception rollbackError)
                {
                    rollbackMessage = " Ошибка отката: " + rollbackError.Message;
                }
            }

            SetStatus("Не удалось установить обновление", ex.Message + rollbackMessage, 0, false);
            _progress.IsIndeterminate = false;
            _percentText.Text = "Текущие пользовательские файлы вне списка пакета не изменялись.";
            _closeButton.Visibility = Visibility.Visible;
            _busy = false;

            // If the launcher was already closed, try to reopen the current (rolled-back, if needed) version.
            if (launcherStopped &&
            launcherPath is not null &&
            File.Exists(launcherPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = launcherPath,
                        WorkingDirectory = installDirectory ?? Path.GetDirectoryName(launcherPath)!,
                        UseShellExecute = true
                    });
                }
                catch { /* Keep the error visible; do not hide the original exception. */ }
            }
        }
    }

    private async Task<List<PackageFile>> StageAndVerifyPackageAsync(string packagePath, string stageDirectory)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("В пакете отсутствует manifest.json.");

        PackageManifest? manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.PackageVersion) || manifest.Files.Count == 0)
            throw new InvalidDataException("manifest.json пуст или имеет неверный формат.");

        var expected = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            var relative = ValidateRelativePath(file.Path);
            if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Некорректная запись манифеста: {relative}");
            if (!expected.TryAdd(relative, file))
                throw new InvalidDataException($"Повторяющийся путь в манифесте: {relative}");
            totalBytes = checked(totalBytes + file.Size);
        }

        var zipFiles = archive.Entries
            .Where(e => e.FullName.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
            .ToList();
        var listedPaths = new HashSet<string>(expected.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zipFiles)
        {
            var relative = ValidateRelativePath(entry.FullName["Files/".Length..]);
            if (!listedPaths.Contains(relative))
                throw new InvalidDataException($"Файл в Files/ отсутствует в манифесте: {relative}");
        }

        var stagedFiles = new List<PackageFile>(manifest.Files.Count);
        long processedBytes = 0;
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var file = manifest.Files[i];
            var relative = ValidateRelativePath(file.Path);
            var entry = archive.GetEntry("Files/" + relative.Replace('\\', '/'))
                ?? throw new InvalidDataException($"В архиве отсутствует файл: {relative}");
            if (entry.Length != file.Size)
                throw new InvalidDataException($"Размер файла не совпадает с манифестом: {relative}");

            var stagedPath = SafeCombine(stageDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            await using var stagedStream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stagedStream));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 файла не совпадает с манифестом: {relative}");

            stagedFiles.Add(file);
            processedBytes += file.Size;
            var percent = totalBytes == 0 ? 60 : (int)Math.Clamp(processedBytes * 60d / totalBytes, 0, 60);
            SetStatus("Проверяем файлы обновления", relative, percent, false);
        }

        if (zipFiles.Count != expected.Count)
            throw new InvalidDataException("Количество файлов в ZIP не совпадает с манифестом.");

        return stagedFiles;
    }

    private static async Task WaitForLauncherExitAsync(int pid)
    {
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }

        using (process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Лаунчер не завершился за 90 секунд. Установка отменена.");
            }
        }
    }

    private static async Task RollBackAsync(string installDirectory, string backupDirectory,
        List<(string Relative, bool HadOriginal)> changed)
    {
        foreach (var item in changed.AsEnumerable().Reverse())
        {
            var target = SafeCombine(installDirectory, item.Relative);
            if (item.HadOriginal)
            {
                var backup = SafeCombine(backupDirectory, item.Relative);
                if (!File.Exists(backup))
                    throw new IOException("Не найдена резервная копия файла: " + item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".twm-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(backup, temp, overwrite: false);
                    File.Move(temp, target, overwrite: true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        await Task.CompletedTask;
    }

    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) ||
            path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("Недопустимый путь в манифесте: " + path);

        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." ||
                              s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Недопустимый путь в манифесте: " + path);

        return string.Join('/', segments);
    }

    private static string SafeCombine(string root, string relative)
    {
        var safeRelative = ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(fullRoot, safeRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы целевой папки: " + relative);
        return combined;
    }

    private void SetStatus(string heading, string detail, int percent, bool indeterminate)
    {
        Dispatcher.Invoke(() =>
        {
            _heading.Text = heading;
            _detail.Text = detail;
            _progress.IsIndeterminate = indeterminate;
            _progress.Value = Math.Clamp(percent, 0, 100);
            _percentText.Text = indeterminate ? "" : $"{Math.Clamp(percent, 0, 100)}%";
        });
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Очистка не должна отменять успешно установленное обновление.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Папку можно будет попробовать удалить при следующем запуске.
        }
    }


    private static void ScheduleCleanupAfterExit(string updatesRoot, int updaterPid)
    {
        try
        {
            string root = Path.GetFullPath(updatesRoot);

            // Экранируем путь для строкового литерала PowerShell.
            string escapedRoot = root.Replace("'", "''");

            string script = $@"
            $targetPid = {updaterPid}
            $root = '{escapedRoot}'

            # Ждём завершения процесса обновлятора.
            while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) {{
                Start-Sleep -Milliseconds 500
            }}

            Start-Sleep -Milliseconds 700

            # Несколько попыток на случай временной блокировки файлов.
            for ($attempt = 0; $attempt -lt 8; $attempt++) {{

                if (Test-Path -LiteralPath $root) {{

                    Get-ChildItem -LiteralPath $root -File -Filter 'update-*.zip' -ErrorAction SilentlyContinue |
                        Remove-Item -Force -ErrorAction SilentlyContinue

                    Get-ChildItem -LiteralPath $root -Directory -Filter 'stage-*' -ErrorAction SilentlyContinue |
                        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

                    Get-ChildItem -LiteralPath $root -Directory -Filter 'Updater-*' -ErrorAction SilentlyContinue |
                        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

                    Get-ChildItem -LiteralPath $root -File -Filter 'TWM Launcher.Updater-*.exe' -ErrorAction SilentlyContinue |
                        Remove-Item -Force -ErrorAction SilentlyContinue

                    # Оставляем одну самую новую резервную копию.
                    $backups = @(
                        Get-ChildItem -LiteralPath $root -Directory -Filter 'rollback-*' -ErrorAction SilentlyContinue |
                            Sort-Object LastWriteTime -Descending
                    )

                    if ($backups.Count -gt 1) {{
                        $backups | Select-Object -Skip 1 | ForEach-Object {{
                            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                        }}
                    }}
                }}

                Start-Sleep -Seconds 2
            }}
            ";

            string encodedScript = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(script));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -WindowStyle Hidden " +
                    "-ExecutionPolicy Bypass -EncodedCommand " + encodedScript,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
        }
        catch
        {
            // Ошибка запуска очистки не отменяет установленное обновление.
        }
    }

}
