
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AMM.App;

public sealed class UpdateService
{
    private const string LatestManifestUrl =
        "https://github.com/TWMandem/TWM-Launcher/releases/latest/download/latest.json";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TWM-Launcher-Updater/1.0");

        return client;
    }

    public sealed class UpdateManifest
    {
        public string PackageVersion { get; set; } = "";
        public string PackageFile { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes)
    {
        public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
    }

    public sealed record UpdateCheckResult(
        bool UpdateAvailable,
        Version CurrentVersion,
        Version LatestVersion,
        UpdateManifest? Manifest = null);

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.GetEntryAssembly();

        var versionText = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "1.0.0";

        versionText = versionText.Split('+')[0];

        if (!Version.TryParse(versionText, out var currentVersion))
        {
            currentVersion = new Version(1, 0, 0);
        }

        using var response = await Http.GetAsync(
            LatestManifestUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        var manifest =
            await JsonSerializer.DeserializeAsync<UpdateManifest>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                cancellationToken);

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.PackageVersion) ||
            string.IsNullOrWhiteSpace(manifest.PackageFile) ||
            string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidDataException(
                "Манифест обновления отсутствует или имеет неверный формат.");
        }

        if (!Version.TryParse(
                manifest.PackageVersion, out var latestVersion))
        {
            throw new InvalidDataException(
                $"Некорректная версия: {manifest.PackageVersion}");
        }

        if (Path.GetFileName(manifest.PackageFile) != manifest.PackageFile ||
            manifest.PackageFile.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException(
                "Недопустимое имя файла пакета.");
        }

        if (manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "Некорректная контрольная сумма SHA-256.");
        }

        return new UpdateCheckResult(
            latestVersion > currentVersion,
            currentVersion,
            latestVersion,
            manifest);
    }

    public async Task<string> DownloadAndVerifyPackageAsync(
        UpdateManifest manifest,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (Path.GetFileName(manifest.PackageFile) != manifest.PackageFile ||
            manifest.PackageFile.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("Недопустимое имя пакета.");
        }

        if (manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Некорректный SHA-256.");
        }

        // Получаем опубликованный размер пакета через GitHub API.
        using var releaseResponse = await Http.GetAsync(
            "https://api.github.com/repos/TWMandem/TWM-Launcher/releases/latest",
            cancellationToken);

        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream =
            await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);

        using var releaseJson =
            await JsonDocument.ParseAsync(
                releaseStream,
                cancellationToken: cancellationToken);

        long expectedSize = -1;

        foreach (var asset in releaseJson.RootElement
                     .GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == manifest.PackageFile)
            {
                expectedSize = asset.GetProperty("size").GetInt64();
                break;
            }
        }

        if (expectedSize <= 0)
        {
            throw new InvalidDataException(
                "Не удалось получить размер пакета из GitHub Releases.");
        }

        var updatesDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TWM Launcher",
            "Updates");

        Directory.CreateDirectory(updatesDirectory);

        var packagePath = Path.Combine(
            updatesDirectory,
            $"update-{Guid.NewGuid():N}.zip");

        var packageUrl =
            "https://github.com/TWMandem/TWM-Launcher/releases/latest/download/"
            + Uri.EscapeDataString(manifest.PackageFile);

        try
        {
            using var response = await Http.GetAsync(
                packageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != expectedSize)
            {
                throw new InvalidDataException(
                    $"GitHub передал Content-Length {contentLength}, " +
                    $"но размер релиза — {expectedSize} байт.");
            }

            await using var source =
                await response.Content.ReadAsStreamAsync(cancellationToken);

            await using (var destination = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;
                var lastProgressReport = System.Diagnostics.Stopwatch.StartNew();

                progress?.Report(new DownloadProgress(0, expectedSize));

                while ((bytesRead = await source.ReadAsync(
                    buffer.AsMemory(), cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;

                    if (totalBytes > expectedSize)
                    {
                        throw new InvalidDataException(
                            "Скачанный файл больше опубликованного пакета.");
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken);

                    if (lastProgressReport.ElapsedMilliseconds >= 100 ||
                        totalBytes == expectedSize)
                    {
                        progress?.Report(new DownloadProgress(totalBytes, expectedSize));
                        lastProgressReport.Restart();
                    }
                }

                await destination.FlushAsync(cancellationToken);
                progress?.Report(new DownloadProgress(expectedSize, expectedSize));

                if (totalBytes != expectedSize)
                {
                    throw new InvalidDataException(
                        $"Загрузка прервана: получено {totalBytes} байт " +
                        $"из {expectedSize}. Повтори попытку.");
                }
            }

            await using var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hashBytes = await SHA256.HashDataAsync(
                packageStream, cancellationToken);

            var actualHash = Convert.ToHexString(hashBytes);

            if (!actualHash.Equals(
                manifest.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "SHA-256 пакета не совпадает с latest.json.");
            }

            return packagePath;
        }
        catch
        {
            try
            {
                if (File.Exists(packagePath))
                    File.Delete(packagePath);
            }
            catch
            {
                // Сохраняем первоначальную ошибку.
            }

            throw;
        }
    }
}
