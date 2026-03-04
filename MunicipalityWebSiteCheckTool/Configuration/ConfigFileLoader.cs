using System.Text.Json;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Serialization;

namespace MunicipalityWebSiteCheckTool.Configuration;

public static class ConfigFileLoader
{
    /// <summary>
    /// feed 共通設定 JSON を読み込む。
    /// ファイルが無い場合は未設定扱いにし、個別設定だけで動かせるようにする。
    /// </summary>
    public static async Task<FeedSettingsConfig?> LoadFeedSettingsAsync(string feedSettingsPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedSettingsPath);

        if (!File.Exists(feedSettingsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(feedSettingsPath);
            return await JsonSerializer.DeserializeAsync(
                       stream,
                       AppJsonContext.Default.FeedSettingsConfig,
                       cancellationToken)
                   ?? throw new InvalidOperationException($"feed settings JSON の解析に失敗しました: {feedSettingsPath}");
        }
        catch (JsonException ex)
        {
            // 共通設定の構文エラー時も対象ファイルが分かるように包み直す。
            throw new InvalidOperationException($"feed settings JSON の解析に失敗しました: {feedSettingsPath}", ex);
        }
    }

    /// <summary>
    /// feeds ディレクトリ配下の JSON を読み込み、重複 ID を検出しながら返す。
    /// 設定不備は起動直後に落としたいので、1 件でも壊れていれば例外にする。
    /// </summary>
    public static async Task<IReadOnlyList<FeedConfig>> LoadFeedsAsync(
        string feedsDirectory,
        FeedSettingsConfig? settings,
        CancellationToken cancellationToken)
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
            FeedConfig config;
            try
            {
                await using var stream = File.OpenRead(file);
                config = await JsonSerializer.DeserializeAsync(
                             stream,
                             AppJsonContext.Default.FeedConfig,
                             cancellationToken)
                         ?? throw new InvalidOperationException($"feeds JSON の解析に失敗しました: {file}");
            }
            catch (JsonException ex)
            {
                // JSON 構文エラー時に対象ファイルが分かるよう、ファイルパス付きで包み直す。
                throw new InvalidOperationException($"feeds JSON の解析に失敗しました: {file}", ex);
            }

            if (!seenIds.Add(config.Id))
            {
                throw new InvalidOperationException($"feeds の id が重複しています: {config.Id}");
            }

            results.Add(ApplyDefaultMatch(config, settings?.DefaultMatch, file));
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
            PageConfig config;
            try
            {
                await using var stream = File.OpenRead(file);
                config = await JsonSerializer.DeserializeAsync(
                             stream,
                             AppJsonContext.Default.PageConfig,
                             cancellationToken)
                         ?? throw new InvalidOperationException($"pages JSON の解析に失敗しました: {file}");
            }
            catch (JsonException ex)
            {
                // JSON 構文エラー時に対象ファイルが分かるよう、ファイルパス付きで包み直す。
                throw new InvalidOperationException($"pages JSON の解析に失敗しました: {file}", ex);
            }

            if (!seenIds.Add(config.Id))
            {
                throw new InvalidOperationException($"pages の id が重複しています: {config.Id}");
            }

            results.Add(config);
        }

        return results;
    }

    /// <summary>
    /// 個別 match が未指定の feed にだけ、共通の defaultMatch を適用する。
    /// どちらも無い場合は監視条件が不明になるため fail-fast とする。
    /// </summary>
    private static FeedConfig ApplyDefaultMatch(FeedConfig config, MatchConfig? defaultMatch, string filePath)
    {
        if (config.Match is not null)
        {
            return config;
        }

        if (defaultMatch is null)
        {
            throw new InvalidOperationException($"match が未指定で、共通 defaultMatch もありません: {filePath}");
        }

        return config with
        {
            Match = defaultMatch
        };
    }
}
