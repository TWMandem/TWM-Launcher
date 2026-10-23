using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Windows.Controls.Primitives;

namespace AMM.App;
 
internal enum LocalBuildKind
{
    FullGame,
    SaFix,
    Mods
}

internal sealed class LocalBuildImportProgress
{
    public int Percent { get; init; }
    public string Stage { get; init; } = "";
    public string CurrentItem { get; init; } = "";
    public long ProcessedBytes { get; init; }
    public long TotalBytes { get; init; }
    public long ProcessedFiles { get; init; }
    public long TotalFiles { get; init; }
}

internal sealed class LocalBuildInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public LocalBuildKind Kind { get; init; }
    public string ContentPath { get; init; } = "";
    public string SourceArchive { get; init; } = "";
    public DateTime ImportedAtUtc { get; init; }
    public long FileCount { get; init; }
    public long SizeBytes { get; init; }

    public string KindLabel => Kind switch
    {
        LocalBuildKind.FullGame => "Полная игра",
        LocalBuildKind.SaFix => "САМОФИКС",
        LocalBuildKind.Mods => "DLL-моды",
        _ => "Сборка"
    };
}

internal sealed class SaFixBackupManifest
{
    public string BuildId { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    public List<SaFixBackupEntry> Entries { get; set; } = new();
}

internal sealed class SaFixBackupEntry
{
    public string RelativePath { get; set; } = "";
    public bool ExistedBeforeInstall { get; set; }
    public int? OriginalAttributes { get; set; }
}

/// <summary>Локальное хранилище сборок. Все пути ограничены каталогом TWMBuilds\Local.</summary>
internal static class LocalBuildStore
{
    private const int MaxEntries = 50000;
    private const long MaxEntryBytes = 20L * 1024 * 1024 * 1024;
    private const long MaxArchiveExpandedBytes = 50L * 1024 * 1024 * 1024;

    // Храним большие игровые архивы вне bin\Debug и каталога приложения:
    // короткие пути снижают риск проблем старых игровых компонентов с путями.
    private static string StorageRoot => @"C:\TWM\Builds";
    public static string BuildsRoot => Path.Combine(StorageRoot, "Local");
    private static string DatabasePath => Path.Combine(StorageRoot, "local-builds.db");
    private static string LegacyStorageRoot => Path.Combine(AppContext.BaseDirectory, "TWMBuilds");

    public static void Initialize()
    {
        MigrateLegacyStorage();
        Directory.CreateDirectory(BuildsRoot);
        Directory.CreateDirectory(StorageRoot);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS LocalBuilds (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Kind TEXT NOT NULL,
    ContentPath TEXT NOT NULL,
    SourceArchive TEXT NOT NULL,
    ImportedAtUtc TEXT NOT NULL,
    FileCount INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_LocalBuilds_ImportedAtUtc ON LocalBuilds(ImportedAtUtc DESC);";
        command.ExecuteNonQuery();
    }

    public static List<LocalBuildInfo> GetAll()
    {
        Initialize();
        var builds = new List<LocalBuildInfo>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Id, Name, Kind, ContentPath, SourceArchive, ImportedAtUtc, FileCount, SizeBytes
                                FROM LocalBuilds ORDER BY ImportedAtUtc DESC;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(0), "N", out _))
                    continue;

                builds.Add(new LocalBuildInfo
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = Enum.TryParse(reader.GetString(2), ignoreCase: true, out LocalBuildKind parsedKind)
                        ? parsedKind : LocalBuildKind.SaFix,
                    ContentPath = reader.GetString(3),
                    SourceArchive = reader.GetString(4),
                    ImportedAtUtc = DateTime.TryParse(reader.GetString(5), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue,
                    FileCount = reader.GetInt64(6),
                    SizeBytes = reader.GetInt64(7)
                });
            }
        }

        // Не позволяем одной устаревшей записи (папка удалена или перемещена)
        // сорвать загрузку всей библиотеки. Запись в БД сохраняем, ничего не удаляем.
        return builds.Where(build =>
        {
            try
            {
                _ = GetContentDirectory(build);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }).ToList();
    }

    public static LocalBuildInfo ImportArchive(
        string archivePath,
        IProgress<LocalBuildImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LocalBuildImportProgress { Percent = 0, Stage = "Подготовка импорта…" });
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Архив не найден.", archivePath);
        string extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Поддерживаются архивы .zip и .rar.");

        string id = Guid.NewGuid().ToString("N");
        string buildDirectory = GetBuildDirectory(id);
        Directory.CreateDirectory(buildDirectory);

        try
        {
            long fileCount = 0;
            long totalBytes = 0;
            long processedBytes = 0;
            long lastReportedBytes = 0;
            long processedFiles = 0;
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                progress?.Report(new LocalBuildImportProgress { Percent = 1, Stage = "Чтение структуры архива…" });
                cancellationToken.ThrowIfCancellationRequested();
                var entries = archive.Entries.ToList();
                if (entries.Count == 0)
                    throw new InvalidDataException("Архив пуст.");
                if (entries.Count > MaxEntries)
                    throw new InvalidDataException($"В архиве слишком много элементов (лимит {MaxEntries:N0}).");

                // Для полной игры разрешаем .asi-компоненты, которые уже входят в сборку.
                // Для САМОФИКСов и DLL-паков ограничения остаются строгими.
                bool isFullGameArchive = entries.Any(entry =>
                {
                    string name = (entry.Key ?? string.Empty).Replace('\\', '/');
                    string fileName = name.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                    return fileName.Equals("gta_sa.exe", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Equals("aggstarter.exe", StringComparison.OrdinalIgnoreCase);
                });
                ValidateCrMpCompatibility(entries, allowAsi: isFullGameArchive);
                long expectedBytes = entries
                    .Where(entry => !entry.IsDirectory && !(entry.Key ?? string.Empty).Replace('\\', '/').EndsWith("/", StringComparison.Ordinal))
                    .GroupBy(entry => entry.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Where(entry => entry.Size > 0)
                    .Sum(entry => entry.Size);
                long expectedFiles = entries
                    .Where(entry => !entry.IsDirectory && !(entry.Key ?? string.Empty).Replace('\\', '/').EndsWith("/", StringComparison.Ordinal))
                    .Select(entry => entry.Key ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase).LongCount();
                progress?.Report(new LocalBuildImportProgress
                {
                    Percent = 3,
                    Stage = $"Распаковка архива · {entries.Count:N0} элементов",
                    TotalBytes = expectedBytes,
                    TotalFiles = expectedFiles
                });

                // Некоторые архиваторы добавляют повторяющиеся записи. На Windows
                // пути сравниваются без учёта регистра; распаковываем первый экземпляр.
                var extractedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string rootWithSeparator = Path.GetFullPath(buildDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string entryName = entry.Key ?? string.Empty;
                    string normalized = entryName.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
                        normalized.Contains(':') || normalized.IndexOf('\0') >= 0)
                        throw new InvalidDataException($"Архив содержит небезопасный путь: {entryName}");

                    string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 0 || segments.Any(part => part == ".." || part == "." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                        throw new InvalidDataException($"Архив содержит небезопасный путь: {entryName}");

                    string relativePath = Path.Combine(segments);
                    string destination = Path.GetFullPath(Path.Combine(buildDirectory, relativePath));
                    if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Путь в архиве выходит за пределы папки сборки: {entryName}");

                    bool isDirectory = entry.IsDirectory || normalized.EndsWith('/');
                    if (isDirectory)
                    {
                        if (extractedFilePaths.Contains(destination))
                            throw new InvalidDataException($"Архив использует один путь одновременно как файл и как папку: {entryName}");

                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    if (Directory.Exists(destination))
                        throw new InvalidDataException($"Архив использует один путь одновременно как файл и как папку: {entryName}");

                    // Некоторые архиваторы добавляют одну запись файла несколько раз.
                    // Повтор не распаковываем: иначе FileMode.CreateNew выдаёт IOException.
                    if (!extractedFilePaths.Add(destination))
                        continue;

                    long entrySize = entry.Size;
                    if (entrySize < 0 || entrySize > MaxEntryBytes)
                        throw new InvalidDataException($"Файл слишком большой: {entryName}");
                    totalBytes = checked(totalBytes + entrySize);
                    if (totalBytes > MaxArchiveExpandedBytes)
                        throw new InvalidDataException("Распакованный архив превышает допустимый лимит 50 ГБ.");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    progress?.Report(new LocalBuildImportProgress
                    {
                        Percent = expectedBytes > 0 ? Math.Min(95, 3 + (int)(processedBytes * 92L / expectedBytes)) : 3,
                        Stage = $"Распаковка файлов · {processedFiles:N0}/{expectedFiles:N0}",
                        CurrentItem = entryName,
                        ProcessedBytes = processedBytes,
                        TotalBytes = expectedBytes,
                        ProcessedFiles = processedFiles,
                        TotalFiles = expectedFiles
                    });
                    using Stream input = entry.OpenEntryStream();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[1024 * 128];
                    long copied = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        copied += read;
                        processedBytes += read;
                        if (processedBytes - lastReportedBytes >= 2L * 1024 * 1024 || (expectedBytes > 0 && processedBytes >= expectedBytes))
                        {
                            lastReportedBytes = processedBytes;
                            progress?.Report(new LocalBuildImportProgress
                            {
                                Percent = expectedBytes > 0 ? Math.Min(95, 3 + (int)(processedBytes * 92L / expectedBytes)) : 3,
                                Stage = $"Распаковка файлов · {processedFiles:N0}/{expectedFiles:N0}",
                                CurrentItem = entryName,
                                ProcessedBytes = processedBytes,
                                TotalBytes = expectedBytes,
                                ProcessedFiles = processedFiles,
                                TotalFiles = expectedFiles
                            });
                        }
                        if (copied > entrySize || copied > MaxEntryBytes)
                            throw new InvalidDataException($"Размер файла в архиве не соответствует заголовку: {entryName}");
                        output.Write(buffer, 0, read);
                    }
                    if (copied != entrySize)
                        throw new InvalidDataException($"Файл в архиве повреждён или обрезан: {entryName}");
                    fileCount++;
                    processedFiles++;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LocalBuildImportProgress { Percent = 96, Stage = "Проверка содержимого и определение типа сборки…", ProcessedFiles = fileCount, TotalFiles = fileCount, ProcessedBytes = totalBytes, TotalBytes = totalBytes });

            // Один проход по дереву файлов вместо трёх отдельных рекурсивных сканирований.
            // На больших сборках это заметно сокращает время после распаковки.
            var importedFileList = new List<string>();
            string? starter = null;
            string? gameExe = null;
            foreach (string filePath in Directory.EnumerateFiles(buildDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                importedFileList.Add(filePath);
                string fileName = Path.GetFileName(filePath);
                if (starter == null && fileName.Equals("aggstarter.exe", StringComparison.OrdinalIgnoreCase))
                    starter = filePath;
                if (gameExe == null && fileName.Equals("gta_sa.exe", StringComparison.OrdinalIgnoreCase))
                    gameExe = filePath;

                if (importedFileList.Count % 5000 == 0)
                {
                    progress?.Report(new LocalBuildImportProgress
                    {
                        Percent = 96,
                        Stage = $"Проверка файлов · {importedFileList.Count:N0} найдено",
                        ProcessedFiles = importedFileList.Count,
                        TotalFiles = fileCount,
                        ProcessedBytes = totalBytes,
                        TotalBytes = totalBytes
                    });
                }
            }
            gameExe = starter ?? gameExe;
            string[] importedFiles = importedFileList.ToArray();
            if (importedFiles.Length == 0)
                throw new InvalidDataException("В архиве не найдено файлов для импорта.");

            LocalBuildKind kind;
            string contentDirectory;

            if (gameExe != null)
            {
                // Полная сборка игры: распознаём по исполняемому файлу.
                kind = LocalBuildKind.FullGame;
                contentDirectory = Path.GetDirectoryName(gameExe)!;
            }
            else if (ContainsDllOnlyPackage(importedFiles))
            {
                // Отдельный DLL-пак: DLL-файлы и необязательные текстовые инструкции.
                kind = LocalBuildKind.Mods;
                contentDirectory = FindCommonContentRoot(buildDirectory);
            }
            else
            {
                // САМОФИКС: набор заменяемых игровых файлов, которому не нужен gta_sa.exe.
                // Он хранится отдельно и не устанавливается автоматически при импорте.
                kind = LocalBuildKind.SaFix;
                contentDirectory = FindCommonContentRoot(buildDirectory);
            }

            string relativeContent = Path.GetRelativePath(buildDirectory, contentDirectory);
            if (relativeContent == ".") relativeContent = "";
            string name = Path.GetFileNameWithoutExtension(archivePath).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = kind switch
            {
                LocalBuildKind.FullGame => "Игровая сборка",
                LocalBuildKind.SaFix => "САМОФИКС",
                _ => "Пак DLL-модов"
            };
            name = name.Length > 100 ? name[..100] : name;

            var result = new LocalBuildInfo
            {
                Id = id,
                Name = name,
                Kind = kind,
                ContentPath = relativeContent,
                SourceArchive = Path.GetFileName(archivePath),
                ImportedAtUtc = DateTime.UtcNow,
                FileCount = fileCount,
                SizeBytes = totalBytes
            };

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LocalBuildImportProgress { Percent = 99, Stage = "Сохранение сборки в библиотеку…", ProcessedFiles = fileCount, TotalFiles = fileCount, ProcessedBytes = totalBytes, TotalBytes = totalBytes });
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO LocalBuilds
                (Id, Name, Kind, ContentPath, SourceArchive, ImportedAtUtc, FileCount, SizeBytes)
                VALUES ($id, $name, $kind, $contentPath, $source, $imported, $files, $bytes);";
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$name", result.Name);
            command.Parameters.AddWithValue("$kind", result.Kind.ToString());
            command.Parameters.AddWithValue("$contentPath", result.ContentPath);
            command.Parameters.AddWithValue("$source", result.SourceArchive);
            command.Parameters.AddWithValue("$imported", result.ImportedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$files", result.FileCount);
            command.Parameters.AddWithValue("$bytes", result.SizeBytes);
            command.ExecuteNonQuery();
            progress?.Report(new LocalBuildImportProgress { Percent = 100, Stage = "Импорт завершён", ProcessedFiles = fileCount, TotalFiles = fileCount, ProcessedBytes = totalBytes, TotalBytes = totalBytes });
            return result;
        }
        catch
        {
            try { if (Directory.Exists(buildDirectory)) Directory.Delete(buildDirectory, true); } catch { }
            throw;
        }
    }

    public static LocalBuildInfo ImportFolder(
        string sourceDirectory,
        IProgress<LocalBuildImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LocalBuildImportProgress { Percent = 0, Stage = "Подготовка импорта папки…" });
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("Выбранная папка не найдена.");

        sourceDirectory = Path.GetFullPath(sourceDirectory);
        string[] sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToArray();
        if (sourceFiles.Length == 0)
            throw new InvalidDataException("В выбранной папке не найдено файлов для импорта.");
        if (sourceFiles.Length > MaxEntries)
            throw new InvalidDataException($"В папке слишком много файлов (лимит {MaxEntries:N0}).");

        bool isFullGameFolder = sourceFiles.Any(path =>
            Path.GetFileName(path).Equals("aggstarter.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("gta_sa.exe", StringComparison.OrdinalIgnoreCase));

        // Не копируем ссылки и junction-точки: они могут вести за пределы выбранной папки.
        foreach (string path in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Импорт папок с символическими ссылками не поддерживается: {path}");
        }
        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Импорт папок с символическими ссылками не поддерживается: {directory}");
        }

        string id = Guid.NewGuid().ToString("N");
        string buildDirectory = GetBuildDirectory(id);
        Directory.CreateDirectory(buildDirectory);
        try
        {
            long totalBytes = sourceFiles.Sum(path => new FileInfo(path).Length);
            if (totalBytes > MaxArchiveExpandedBytes)
                throw new InvalidDataException("Общий размер папки превышает допустимый лимит 50 ГБ.");
            long copiedBytes = 0;
            long copiedFiles = 0;
            foreach (string sourceFile in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(sourceDirectory, sourceFile);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".." || part == "." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException($"Недопустимый путь файла: {relative}");
                string destination = Path.GetFullPath(Path.Combine(buildDirectory, relative));
                string buildPrefix = Path.GetFullPath(buildDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(buildPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Путь выходит за пределы папки сборки: {relative}");

                string extension = Path.GetExtension(sourceFile);
                if (new FileInfo(sourceFile).Length > MaxEntryBytes)
                    throw new InvalidDataException($"Файл превышает допустимый размер 20 ГБ: {relative}");
                string[] forbiddenDirectories = { "cleo", "modloader", "scripts", "moonloader" };
                string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Take(Math.Max(0, segments.Length - 1)).Any(part => forbiddenDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Папка содержит неподдерживаемый компонент: {relative}. CLEO, ModLoader, scripts и MoonLoader не импортируются.");
                if (!isFullGameFolder && extension.Equals(".asi", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Файл .asi не поддерживается при импорте папки: {relative}. Для CR:MP используй DLL-моды.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[128 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        copiedBytes += read;
                    }
                }
                copiedFiles++;
                if (copiedFiles % 250 == 0 || copiedFiles == sourceFiles.Length)
                {
                    int percent = totalBytes > 0 ? Math.Min(95, 3 + (int)(copiedBytes * 92L / totalBytes)) : Math.Min(95, 3 + (int)(copiedFiles * 92L / sourceFiles.Length));
                    progress?.Report(new LocalBuildImportProgress { Percent = percent, Stage = $"Копирование файлов · {copiedFiles:N0}/{sourceFiles.Length:N0}", CurrentItem = relative, ProcessedBytes = copiedBytes, TotalBytes = totalBytes, ProcessedFiles = copiedFiles, TotalFiles = sourceFiles.Length });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LocalBuildImportProgress { Percent = 96, Stage = "Проверка содержимого папки…", ProcessedFiles = copiedFiles, TotalFiles = sourceFiles.Length, ProcessedBytes = copiedBytes, TotalBytes = totalBytes });
            string[] importedFiles = Directory.EnumerateFiles(buildDirectory, "*", SearchOption.AllDirectories).ToArray();
            string? gameExe = importedFiles.FirstOrDefault(file => Path.GetFileName(file).Equals("aggstarter.exe", StringComparison.OrdinalIgnoreCase))
                ?? importedFiles.FirstOrDefault(file => Path.GetFileName(file).Equals("gta_sa.exe", StringComparison.OrdinalIgnoreCase));
            LocalBuildKind kind = gameExe != null ? LocalBuildKind.FullGame : ContainsDllOnlyPackage(importedFiles) ? LocalBuildKind.Mods : LocalBuildKind.SaFix;
            string contentDirectory = gameExe != null ? Path.GetDirectoryName(gameExe)! : FindCommonContentRoot(buildDirectory);
            string relativeContent = Path.GetRelativePath(buildDirectory, contentDirectory);
            if (relativeContent == ".") relativeContent = "";
            string name = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = kind == LocalBuildKind.FullGame ? "Игровая сборка" : kind == LocalBuildKind.SaFix ? "САМОФИКС" : "Пак DLL-модов";
            name = name.Length > 100 ? name[..100] : name;

            var result = new LocalBuildInfo { Id = id, Name = name, Kind = kind, ContentPath = relativeContent, SourceArchive = "Папка: " + name, ImportedAtUtc = DateTime.UtcNow, FileCount = copiedFiles, SizeBytes = copiedBytes };
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LocalBuildImportProgress { Percent = 99, Stage = "Сохранение сборки в библиотеку…", ProcessedFiles = copiedFiles, TotalFiles = copiedFiles, ProcessedBytes = copiedBytes, TotalBytes = totalBytes });
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO LocalBuilds (Id, Name, Kind, ContentPath, SourceArchive, ImportedAtUtc, FileCount, SizeBytes) VALUES ($id, $name, $kind, $contentPath, $source, $imported, $files, $bytes);";
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$name", result.Name);
            command.Parameters.AddWithValue("$kind", result.Kind.ToString());
            command.Parameters.AddWithValue("$contentPath", result.ContentPath);
            command.Parameters.AddWithValue("$source", result.SourceArchive);
            command.Parameters.AddWithValue("$imported", result.ImportedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$files", result.FileCount);
            command.Parameters.AddWithValue("$bytes", result.SizeBytes);
            command.ExecuteNonQuery();
            progress?.Report(new LocalBuildImportProgress { Percent = 100, Stage = "Импорт завершён", ProcessedFiles = copiedFiles, TotalFiles = copiedFiles, ProcessedBytes = copiedBytes, TotalBytes = totalBytes });
            return result;
        }
        catch
        {
            try { if (Directory.Exists(buildDirectory)) Directory.Delete(buildDirectory, true); } catch { }
            throw;
        }
    }

    public static string GetContentDirectory(LocalBuildInfo build)
    {
        string buildDirectory = GetBuildDirectory(build.Id);
        string candidate = string.IsNullOrWhiteSpace(build.ContentPath)
            ? buildDirectory
            : Path.GetFullPath(Path.Combine(buildDirectory, build.ContentPath));
        string root = Path.GetFullPath(buildDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(Path.GetFullPath(buildDirectory), StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь сборки в базе данных недопустим.");
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException("Папка импортированной сборки не найдена.");
        return candidate;
    }

    public static (int InstalledFiles, int BackedUpFiles, string BackupDirectory) InstallSaFix(LocalBuildInfo build, string targetGameDirectory)
    {
        Initialize();
        if (build.Kind != LocalBuildKind.SaFix)
            throw new InvalidOperationException("Устанавливать таким способом можно только САМОФИКС.");
        if (string.IsNullOrWhiteSpace(targetGameDirectory) || !Directory.Exists(targetGameDirectory))
            throw new DirectoryNotFoundException("Папка игры не найдена. Сначала укажи папку игры в настройках лаунчера.");

        string sourceRoot = Path.GetFullPath(GetContentDirectory(build));
        string targetRoot = Path.GetFullPath(targetGameDirectory);
        string targetPrefix = targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string backupsRoot = Path.Combine(StorageRoot, "Backups");
        Directory.CreateDirectory(backupsRoot);
        string backupRoot = Path.Combine(backupsRoot, $"{build.Id}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        var manifest = new SaFixBackupManifest { BuildId = build.Id, TargetRoot = targetRoot };
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("В САМОФИКСе нет файлов для установки.");

        var processed = new List<(string Destination, string? Backup, bool Existed, FileAttributes? OriginalAttributes)>();
        int backedUp = 0;
        try
        {
            foreach (string sourceFile in files)
            {
                string relative = Path.GetRelativePath(sourceRoot, sourceFile);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part == ".." || part == "."))
                    throw new InvalidDataException($"Недопустимый путь файла САМОФИКСа: {relative}");

                string destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!destination.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Файл выходит за пределы папки игры: {relative}");
                if (destination.StartsWith(Path.GetFullPath(BuildsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("САМОФИКС не может перезаписывать внутреннее хранилище TWM Launcher.");

                bool existed = File.Exists(destination);
                string? backup = null;
                FileAttributes? originalAttributes = null;
                if (existed) originalAttributes = File.GetAttributes(destination);
                manifest.Entries.Add(new SaFixBackupEntry
                {
                    RelativePath = relative,
                    ExistedBeforeInstall = existed,
                    OriginalAttributes = originalAttributes.HasValue ? (int)originalAttributes.Value : null
                });
                if (existed)
                {
                    // Сначала сохраняем оригинал. Затем снимаем атрибут ReadOnly,
                    // который иначе может вызвать UnauthorizedAccessException при перезаписи.
                    backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    backedUp++;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                processed.Add((destination, backup, existed, originalAttributes));
                if (existed && originalAttributes.HasValue &&
                    (originalAttributes.Value & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destination, originalAttributes.Value & ~FileAttributes.ReadOnly);
                }
                File.Copy(sourceFile, destination, overwrite: true);
                if (existed && originalAttributes.HasValue)
                    File.SetAttributes(destination, originalAttributes.Value);
            }

            File.WriteAllText(Path.Combine(backupRoot, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return (files.Length, backedUp, backupRoot);
        }
        catch
        {
            // Откатываем уже обработанные файлы, сохраняя исходное состояние игры.
            for (int i = processed.Count - 1; i >= 0; i--)
            {
                var item = processed[i];
                try
                {
                    if (item.Existed && item.Backup != null && File.Exists(item.Backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                        if (File.Exists(item.Destination))
                        {
                            FileAttributes currentAttributes = File.GetAttributes(item.Destination);
                            if ((currentAttributes & FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(item.Destination, currentAttributes & ~FileAttributes.ReadOnly);
                        }
                        File.Copy(item.Backup, item.Destination, overwrite: true);
                        if (item.OriginalAttributes.HasValue)
                            File.SetAttributes(item.Destination, item.OriginalAttributes.Value);
                    }
                    else if (!item.Existed && File.Exists(item.Destination))
                    {
                        File.Delete(item.Destination);
                    }
                }
                catch { /* Оставляем резервные копии для ручного восстановления. */ }
            }
            throw;
        }
    }

    public static bool HasRestorableSaFix(LocalBuildInfo build)
    {
        if (build.Kind != LocalBuildKind.SaFix) return false;
        try
        {
            _ = GetRestorableSaFixTargetDirectory(build);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the game directory saved when the latest unrestored copy of this SA-fix was installed.</summary>
    public static string GetRestorableSaFixTargetDirectory(LocalBuildInfo build)
    {
        Initialize();
        if (build.Kind != LocalBuildKind.SaFix)
            throw new InvalidOperationException("Возвращать стандартные файлы можно только для САМОФИКСа.");

        string backupsRoot = Path.Combine(StorageRoot, "Backups");
        if (!Directory.Exists(backupsRoot))
            throw new InvalidOperationException("Для этого САМОФИКСа резервная копия не найдена.");

        foreach (string backupRoot in Directory.EnumerateDirectories(backupsRoot)
            .Where(path => Path.GetFileName(path).StartsWith(build.Id + "_", StringComparison.OrdinalIgnoreCase))
            .Where(path => !File.Exists(Path.Combine(path, ".restored")))
            .OrderByDescending(path => Directory.GetCreationTimeUtc(path)))
        {
            string manifestPath = Path.Combine(backupRoot, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            SaFixBackupManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<SaFixBackupManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Не удалось прочитать сведения о резервной копии САМОФИКСа.", ex);
            }

            if (manifest == null || !string.Equals(manifest.BuildId, build.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл сведений о резервной копии повреждён или относится к другой сборке.");
            if (string.IsNullOrWhiteSpace(manifest.TargetRoot))
                throw new InvalidDataException("В резервной копии не сохранён путь папки игры.");

            string targetRoot = Path.GetFullPath(manifest.TargetRoot);
            if (!Directory.Exists(targetRoot))
                throw new DirectoryNotFoundException("Папка игры, в которую был установлен САМОФИКС, больше не найдена:\n" + targetRoot);
            return targetRoot;
        }

        bool hasLegacyBackup = Directory.EnumerateDirectories(backupsRoot)
            .Any(path => !File.Exists(Path.Combine(path, "manifest.json")) &&
                         !File.Exists(Path.Combine(path, ".restored")));
        if (hasLegacyBackup)
            throw new InvalidOperationException(
                "Эта резервная копия создана старой версией TWM Launcher, которая не сохраняла путь игры. " +
                "Чтобы безопасно включить восстановление в один клик, установи этот САМОФИКС ещё раз после обновления, " +
                "а затем нажми «Вернуть стандартные файлы».");

        throw new InvalidOperationException("Для этого САМОФИКСа нет неиспользованной резервной копии. Сначала установи его в игру.");
    }

    public static (int RestoredFiles, int RemovedFiles, string BackupDirectory) RestoreLatestSaFix(LocalBuildInfo build)
    {
        Initialize();
        string targetRoot = GetRestorableSaFixTargetDirectory(build);
        string backupsRoot = Path.Combine(StorageRoot, "Backups");
        string? backupRoot = null;
        SaFixBackupManifest? manifest = null;

        foreach (string candidate in Directory.EnumerateDirectories(backupsRoot)
            .Where(path => Path.GetFileName(path).StartsWith(build.Id + "_", StringComparison.OrdinalIgnoreCase))
            .Where(path => !File.Exists(Path.Combine(path, ".restored")))
            .OrderByDescending(path => Directory.GetCreationTimeUtc(path)))
        {
            string manifestPath = Path.Combine(candidate, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var candidateManifest = JsonSerializer.Deserialize<SaFixBackupManifest>(File.ReadAllText(manifestPath));
            if (candidateManifest == null || !string.Equals(candidateManifest.BuildId, build.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл сведений о резервной копии повреждён.");
            if (string.Equals(Path.GetFullPath(candidateManifest.TargetRoot), targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                backupRoot = candidate;
                manifest = candidateManifest;
                break;
            }
        }

        if (backupRoot == null || manifest == null)
            throw new InvalidOperationException("Не удалось найти резервную копию САМОФИКСа для сохранённой папки игры.");

        string targetPrefix = targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int restored = 0, removed = 0;
        foreach (var entry in manifest.Entries)
        {
            string relative = entry.RelativePath;
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".." || part == "."))
                throw new InvalidDataException("Недопустимый путь в резервной копии.");
            string destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
            if (!destination.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь восстановления выходит за пределы папки игры.");
            string backupFile = Path.Combine(backupRoot, relative);

            if (entry.ExistedBeforeInstall && File.Exists(backupFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                File.Copy(backupFile, destination, overwrite: true);
                if (entry.OriginalAttributes.HasValue)
                    File.SetAttributes(destination, (FileAttributes)entry.OriginalAttributes.Value);
                restored++;
            }
            else if (!entry.ExistedBeforeInstall && File.Exists(destination))
            {
                FileAttributes attrs = File.GetAttributes(destination);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
                File.Delete(destination);
                removed++;
            }
        }

        File.WriteAllText(Path.Combine(backupRoot, ".restored"), DateTime.UtcNow.ToString("O"));
        return (restored, removed, backupRoot);
    }

    public static void Delete(LocalBuildInfo build)
    {
        Initialize();
        string directory = GetBuildDirectory(build.Id);
        string root = Path.GetFullPath(BuildsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Удаление за пределами хранилища сборок запрещено.");

        // Удаляем файлы первыми: если Windows откажет в доступе, запись в БД останется.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM LocalBuilds WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", build.Id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void MigrateLegacyStorage()
    {
        string legacyRoot = LegacyStorageRoot;
        string legacyBuilds = Path.Combine(legacyRoot, "Local");
        string legacyDatabase = Path.Combine(legacyRoot, "local-builds.db");
        string newBuilds = BuildsRoot;
        string newDatabase = DatabasePath;

        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(newBuilds);

        // Переносим уже импортированные сборки, чтобы библиотека сохранилась после обновления.
        if (Directory.Exists(legacyBuilds))
        {
            foreach (string oldDirectory in Directory.EnumerateDirectories(legacyBuilds))
            {
                string destination = Path.Combine(newBuilds, Path.GetFileName(oldDirectory));
                if (Directory.Exists(destination)) continue;
                try { Directory.Move(oldDirectory, destination); }
                catch
                {
                    try { CopyDirectory(oldDirectory, destination); } catch { }
                }
            }
        }

        if (!File.Exists(newDatabase) && File.Exists(legacyDatabase))
        {
            try { File.Copy(legacyDatabase, newDatabase, overwrite: false); } catch { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string GetBuildDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidOperationException("Некорректный идентификатор сборки.");
        string root = Path.GetFullPath(BuildsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(BuildsRoot, id));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Недопустимый путь сборки.");
        return directory;
    }

    private static void ValidateCrMpCompatibility(IReadOnlyCollection<SharpCompress.Archives.IArchiveEntry> entries, bool allowAsi)
    {
        string[] unsupportedDirectories = { "cleo", "modloader", "scripts", "moonloader" };
        var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            string normalized = (entry.Key ?? string.Empty).Replace('\\', '/');
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool isDirectory = entry.IsDirectory || normalized.EndsWith("/", StringComparison.Ordinal);
            int directorySegmentCount = isDirectory ? segments.Length : Math.Max(0, segments.Length - 1);

            for (int i = 0; i < directorySegmentCount; i++)
            {
                if (unsupportedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                    unsupported.Add(segments[i]);
            }

            if (!allowAsi && !isDirectory && string.Equals(Path.GetExtension(segments.LastOrDefault() ?? string.Empty), ".asi", StringComparison.OrdinalIgnoreCase))
                unsupported.Add(".asi");
        }

        if (unsupported.Count > 0)
        {
            throw new InvalidDataException(
                "Архив содержит неподдерживаемые для AMAZING ONLINE / CR:MP компоненты: " +
                string.Join(", ", unsupported) + ".\n\n" +
                "CLEO, ModLoader, папки scripts, MoonLoader и файлы .asi не импортируются. " +
                "Для мод-паков используй DLL-файлы. Удали неподдерживаемые компоненты из архива и повтори импорт.");
        }
    }

    private static bool ContainsDllOnlyPackage(string[] files)
    {
        bool hasDll = files.Any(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase));
        if (!hasDll) return false;

        // DLL-пак не должен маскировать САМОФИКС, содержащий игровые ресурсы.
        // Текстовые файлы разрешены для README/LICENSE/инструкций.
        string[] allowedExtensions = { ".dll", ".txt", ".md", ".nfo", ".rtf" };
        return files.All(file => allowedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
    }

    private static string FindCommonContentRoot(string root)
    {
        // Убираем внешние каталоги-обёртки, например archive\PC\audio\..., 
        // но не срезаем игровые каталоги audio/data/models.
        string[] gameRootNames =
        {
            "audio", "data", "models", "text", "anim", "aggvoice", "amazing",
            "amazing_cef", "asi", "enbseries", "redist", "tools", "cleo",
            "modloader", "scripts", "moonloader"
        };

        string current = root;
        while (true)
        {
            string[] files = Directory.GetFiles(current);
            string[] dirs = Directory.GetDirectories(current);
            if (files.Length != 0 || dirs.Length != 1)
                return current;

            string childName = Path.GetFileName(dirs[0]);
            if (gameRootNames.Contains(childName, StringComparer.OrdinalIgnoreCase))
                return current;

            current = dirs[0];
        }
    }
}
