using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Serialization;

namespace MunicipalityWebSiteCheckTool.State;

public class StateStore
{
    private static readonly AppJsonContext StateJsonContext = new(new JsonSerializerOptions(AppJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    });

    private string? _stateDir;

    public void Initialize(string stateDir)
    {
        if (string.IsNullOrWhiteSpace(stateDir))
        {
            throw new ArgumentException("state ディレクトリが未指定です。", nameof(stateDir));
        }

        Directory.CreateDirectory(stateDir);
        _stateDir = stateDir;
    }

    public async Task<FeedState?> LoadAsync(string feedId, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var path = Path.Combine(_stateDir!, $"{feedId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
                   stream,
                   AppJsonContext.Default.FeedState,
                   cancellationToken)
               ?? throw new InvalidOperationException($"state JSON の解析に失敗: {path}");
    }

    public async Task SaveAsync(string feedId, FeedState state, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var path = Path.Combine(_stateDir!, $"{feedId}.json");
        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(state, StateJsonContext.FeedState);

        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<PageState?> LoadPageAsync(string pageId, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var path = Path.Combine(_stateDir!, $"{pageId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
                   stream,
                   AppJsonContext.Default.PageState,
                   cancellationToken)
               ?? throw new InvalidOperationException($"page state JSON の解析に失敗: {path}");
    }

    public async Task SavePageAsync(string pageId, PageState state, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var path = Path.Combine(_stateDir!, $"{pageId}.json");
        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(state, StateJsonContext.PageState);

        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private void EnsureInitialized()
    {
        if (string.IsNullOrWhiteSpace(_stateDir))
        {
            throw new InvalidOperationException(
                "StateStore.Initialize() が呼ばれていません。state ディレクトリを先に初期化してください。");
        }
    }
}
