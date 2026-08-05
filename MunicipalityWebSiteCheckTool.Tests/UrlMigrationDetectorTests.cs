using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Processing;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class UrlMigrationDetectorTests
{
    [Fact]
    public void Detect_MetaRefresh_ReturnHighConfidenceHint()
    {
        const string html = """
            <html>
              <head>
                <meta http-equiv="refresh" content="5; url=/new-path">
              </head>
              <body>移行しました。</body>
            </html>
            """;

        var hint = UrlMigrationDetector.Detect(html, "https://example.com/old");

        Assert.NotNull(hint);
        Assert.Equal(UrlMigrationReasons.MetaRefresh, hint!.Reason);
        Assert.Equal(UrlMigrationConfidences.High, hint.Confidence);
        Assert.Equal("https://example.com/new-path", hint.CandidateUrl);
    }

    [Fact]
    public void Detect_JavaScriptRedirect_ReturnHighConfidenceHint()
    {
        const string html = """
            <html>
              <body>
                <script>
                  window.location.replace('/next');
                </script>
              </body>
            </html>
            """;

        var hint = UrlMigrationDetector.Detect(html, "https://example.com/current");

        Assert.NotNull(hint);
        Assert.Equal(UrlMigrationReasons.JavaScriptRedirect, hint!.Reason);
        Assert.Equal(UrlMigrationConfidences.High, hint.Confidence);
        Assert.Equal("https://example.com/next", hint.CandidateUrl);
    }

    [Fact]
    public void Detect_MigrationLink_ReturnMediumConfidenceHint()
    {
        const string html = """
            <html>
              <body>
                <main>
                  <p>このページは新サイトへ移行しました。<a href="/next">こちらをクリックしてください</a></p>
                </main>
              </body>
            </html>
            """;

        var hint = UrlMigrationDetector.Detect(html, "https://example.com/current");

        Assert.NotNull(hint);
        Assert.Equal(UrlMigrationReasons.MigrationLink, hint!.Reason);
        Assert.Equal(UrlMigrationConfidences.Medium, hint.Confidence);
        Assert.Equal("https://example.com/next", hint.CandidateUrl);
    }

    [Fact]
    public void Detect_MigrationTextOnly_ReturnLowConfidenceHint()
    {
        const string html = """
            <html>
              <head><title>このページは新サイトへ移行しました</title></head>
              <body>
                <main>このURLは閉鎖します。</main>
              </body>
            </html>
            """;

        var hint = UrlMigrationDetector.Detect(html, "https://example.com/current");

        Assert.NotNull(hint);
        Assert.Equal(UrlMigrationReasons.MigrationTextOnly, hint!.Reason);
        Assert.Equal(UrlMigrationConfidences.Low, hint.Confidence);
        Assert.Null(hint.CandidateUrl);
    }
}
