using System.Text.Json;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Serialization;

namespace MunicipalityWebSiteCheckTool.Configuration;

public static class ConfigFileLoader
{
    /// <summary>
    /// feeds ディレクトリ配下の JSON を読み込み、重複 ID を検出しながら返す。
    /// 設定不備は起動直後に落としたいので、1 件でも壊れていれば例外にする。
    /// </summary>
    public static async Task<IReadOnlyList<FeedConfig>> LoadFeedsAsync(string feedsDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedsDirectory);

        if (!Directory.Exists(feedsDirectory))
        {
            throw new DirectoryNotFoundException($"feeds ディレクトリが見つかりません: {feedsDirectory}");
        }

        var files = Directory.GetFiles(feedsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<FeedConfig>(files.Length);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var config = await JsonSerializer.DeserializeAsync(
                             stream,
                             AppJsonContext.Default.FeedConfig,
                             cancellationToken)
                         ?? throw new InvalidOperationException($"feeds JSON の解析に失敗しました: {file}");

            if (!seenIds.Add(config.Id))
            {
                throw new InvalidOperationException($"feeds の id が重複しています: {config.Id}");
            }

            results.Add(config);
        }

        return results;
    }

    /// <summary>
    /// pages ディレクトリ配下の JSON を読み込み、重複 ID を検出しながら返す。
    /// page モードの前提になるので、ここも fail-fast で扱う。
    /// </summary>
    public static async Task<IReadOnlyList<PageConfig>> LoadPagesAsync(string pagesDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagesDirectory);

        if (!Directory.Exists(pagesDirectory))
        {
            throw new DirectoryNotFoundException($"pages ディレクトリが見つかりません: {pagesDirectory}");
        }

        var files = Directory.GetFiles(pagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<PageConfig>(files.Length);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var config = await JsonSerializer.DeserializeAsync(
                             stream,
                             AppJsonContext.Default.PageConfig,
                             cancellationToken)
                         ?? throw new InvalidOperationException($"pages JSON の解析に失敗しました: {file}");

            if (!seenIds.Add(config.Id))
            {
                throw new InvalidOperationException($"pages の id が重複しています: {config.Id}");
            }

            results.Add(config);
        }

        return results;
    }
}
