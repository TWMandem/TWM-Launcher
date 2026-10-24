using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AMM.App;

internal sealed class AmazingServerStatsService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const string TokenRegistryPath = @"Software\TWM Launcher";
    private const string ProtectedTokenValueName = "ServerStatsTokenProtected";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("TWM Launcher Amazing Online Server Stats v1");
    private static readonly string LegacyTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TWM Launcher", "server-stats-token.txt");

    // User-supplied default value that was confirmed in the previous working build.
    // It is a client-side fallback, not a secret: anyone with source or binary access can extract it.
    private const string BundledStatsToken = "078c567b29ab155be9ac8bb04d0ba6f7df893c4e26d48fc941994b69736116b9";

    private static readonly string[] ApiHosts =
    {
        "trinity-fb201.api-amazing.com",
        "trinity-fb101.api-amazing.com",
        "trinity.api-amazing.com"
    };

    public async Task<ServerStatsFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var tokens = new List<string>();
        AddCandidate(tokens, await TryReadLegacyTokenFileAsync(cancellationToken));
        AddCandidate(tokens, BundledStatsToken);
        AddCandidate(tokens, TryReadTokenFromAmazingLauncherRegistry());
        AddCandidate(tokens, TryReadProtectedToken());

        if (tokens.Count == 0)
            return ServerStatsFetchResult.Failure("Статистика серверов сейчас недоступна. Повторим попытку через 30 секунд.");

        foreach (string token in tokens)
        {
            foreach (string host in ApiHosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var builder = new UriBuilder
                    {
                        Scheme = "https",
                        Host = host,
                        Path = "/russia/launcher/servers.stats",
                        Query = "token=" + Uri.EscapeDataString(token)
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                    request.Headers.TryAddWithoutValidation("R-Source", "LAUNCHER");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    request.Headers.UserAgent.ParseAdd("Amazing Launcher (2024)");

                    using HttpResponseMessage response = await Client.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    string body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(body))
                        continue;

                    using JsonDocument document = JsonDocument.Parse(body);
                    JsonElement root = document.RootElement;
                    JsonElement payload = root;
                    if (TryGetPropertyIgnoreCase(root, "response", out JsonElement responseElement))
                        payload = responseElement;
                    if (!TryGetPropertyIgnoreCase(payload, "servers", out JsonElement serversElement) ||
                        serversElement.ValueKind != JsonValueKind.Array)
                        continue;

                    var servers = new List<AmazingServerStat>();
                    foreach (JsonElement item in serversElement.EnumerateArray())
                    {
                        string name = GetString(item, "name").Trim().ToUpperInvariant();
                        int online = GetInt32(item, "online");
                        int maxOnline = GetInt32(item, "max_online");
                        int queue = GetInt32(item, "queue");
                        if (name.Length == 0 || online < 0 || maxOnline <= 0 || online > maxOnline)
                            continue;

                        servers.Add(new AmazingServerStat(
                            name,
                            GetString(item, "ip"),
                            GetInt32(item, "port"),
                            online,
                            maxOnline,
                            Math.Max(0, queue),
                            GetString(item, "status")));
                    }

                    if (servers.Count > 0)
                    {
                        TrySaveProtectedToken(token);
                        TryDeleteLegacyTokenFile();
                        return ServerStatsFetchResult.SuccessResult(servers, DateTimeOffset.Now);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Don't log request URLs or exceptions: URLs contain the API token.
                }
            }
        }

        return ServerStatsFetchResult.Failure("Статистика серверов сейчас недоступна. Повторим попытку через 30 секунд.");
    }

    private static void AddCandidate(List<string> tokens, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token) && IsPlausibleToken(token) && !tokens.Contains(token, StringComparer.Ordinal))
            tokens.Add(token);
    }

    private static bool IsPlausibleToken(string token) =>
        token.Length is >= 32 and <= 512 && token.All(c =>
            c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c is '-' or '_' or '.');

    private static string? TryReadTokenFromAmazingLauncherRegistry()
    {
        string[] paths = { @"SOFTWARE\Amazing\Launcher", @"SOFTWARE\WOW6432Node\Amazing\Launcher" };
        string[] names = { "Token", "auth_token", "AuthToken", "token" };
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string path in paths)
                {
                    using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
                    if (key is null) continue;
                    foreach (string name in names)
                    {
                        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                        {
                            value = value.Trim();
                            if (IsPlausibleToken(value)) return value;
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static string? TryReadProtectedToken()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(TokenRegistryPath, writable: false);
            if (key?.GetValue(ProtectedTokenValueName) is not byte[] protectedBytes || protectedBytes.Length == 0)
                return null;
            byte[]? clearBytes = null;
            try
            {
                clearBytes = ProtectedData.Unprotect(protectedBytes, TokenEntropy, DataProtectionScope.CurrentUser);
                string token = Encoding.UTF8.GetString(clearBytes).Trim();
                return IsPlausibleToken(token) ? token : null;
            }
            finally
            {
                if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        catch { return null; }
    }

    private static void TrySaveProtectedToken(string token)
    {
        byte[]? plain = null;
        byte[]? protectedBytes = null;
        try
        {
            plain = Encoding.UTF8.GetBytes(token);
            protectedBytes = ProtectedData.Protect(plain, TokenEntropy, DataProtectionScope.CurrentUser);
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(TokenRegistryPath, writable: true);
            key?.SetValue(ProtectedTokenValueName, protectedBytes, RegistryValueKind.Binary);
        }
        catch { }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static async Task<string?> TryReadLegacyTokenFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(LegacyTokenPath)) return null;
            string value = (await File.ReadAllTextAsync(LegacyTokenPath, cancellationToken)).Trim();
            return IsPlausibleToken(value) ? value : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static void TryDeleteLegacyTokenFile()
    {
        try { if (File.Exists(LegacyTokenPath)) File.Delete(LegacyTokenPath); } catch { }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
            }
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int GetInt32(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return 0;
    }
}

internal sealed record AmazingServerStat(string Name, string Ip, int Port, int Online, int MaxOnline, int Queue, string Status);

internal sealed record ServerStatsFetchResult(
    bool Success,
    IReadOnlyList<AmazingServerStat> Servers,
    string Message,
    DateTimeOffset? UpdatedAt)
{
    public static ServerStatsFetchResult SuccessResult(IReadOnlyList<AmazingServerStat> servers, DateTimeOffset updatedAt) =>
        new(true, servers, string.Empty, updatedAt);
    public static ServerStatsFetchResult Failure(string message) =>
        new(false, Array.Empty<AmazingServerStat>(), message, null);
}
