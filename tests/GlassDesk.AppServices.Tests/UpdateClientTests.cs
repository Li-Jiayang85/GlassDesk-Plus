using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 更新通道的解析与校验必须对同一个资产名达成一致。
/// </summary>
/// <remarks>
/// 这里锁住的是一个真实交付事故：<c>ParseRelease</c> 认定资产名是
/// <c>GlassDesk-Plus-&lt;版本&gt;-win-x64-Setup.exe</c>，而下载前的 <c>ValidateInstaller</c>
/// 却要求 <c>GlassDesk-&lt;版本&gt;-win-x64-Setup.exe</c>（漏了 Plus）。
/// 结果是「检查更新」能发现新版本，一点下载就报「安装包信息无效」——
/// 两端各自看都合理，合起来必然失败，所以必须有断言把两者钉在一起。
/// </remarks>
public sealed class UpdateClientTests
{
    private static string ReleaseJson(string tag, string assetName, string digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000")
        => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/Li-Jiayang85/GlassDesk-Plus/releases/tag/{{tag}}",
          "body": "notes",
          "assets": [
            {
              "name": "{{assetName}}",
              "state": "uploaded",
              "size": 53950502,
              "digest": "{{digest}}",
              "browser_download_url": "https://github.com/Li-Jiayang85/GlassDesk-Plus/releases/download/{{tag}}/{{assetName}}"
            }
          ]
        }
        """;

    private static readonly Uri Source = new("https://api.github.com/repos/Li-Jiayang85/GlassDesk-Plus/releases/latest");

    [Fact]
    public void 带Plus的资产名能被解析且能通过校验()
    {
        var json = ReleaseJson("v1.4.0", "GlassDesk-Plus-1.4.0-win-x64-Setup.exe");
        var release = UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source);

        Assert.NotNull(release);
        Assert.Equal("GlassDesk-Plus-1.4.0-win-x64-Setup.exe", release!.FileName);
        UpdateClient.ValidateInstaller(release);
    }

    [Fact]
    public void 不带Plus的资产名不会被解析()
    {
        // 旧命名不该再被接受：解析阶段就该拦下，而不是等到下载前才报错。
        var json = ReleaseJson("v1.4.0", "GlassDesk-1.4.0-win-x64-Setup.exe");
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source));
    }

    [Fact]
    public void 解析出的资产名与校验要求的是同一个()
    {
        // 直接对比两处的期望，任何一边改了名字而另一边没跟上，这里就会红。
        const string version = "1.4.0";
        var json = ReleaseJson($"v{version}", $"GlassDesk-Plus-{version}-win-x64-Setup.exe");
        var release = UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source)!;

        var fromParse = release.FileName;
        UpdateClient.ValidateInstaller(release); // 名字不一致时这里会抛「安装包信息无效」

        Assert.Equal(fromParse, release.FileName);
        Assert.EndsWith($"-win-x64-Setup.exe", fromParse, StringComparison.Ordinal);
        Assert.StartsWith("GlassDesk-Plus-", fromParse, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺少校验摘要的资产会被拒()
    {
        var json = ReleaseJson("v1.4.0", "GlassDesk-Plus-1.4.0-win-x64-Setup.exe", digest: "");
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source));
    }

    [Theory]
    [InlineData("v1.0.4", false)]
    [InlineData("v1.0.5", false)]
    [InlineData("v1.3.1", false)]
    [InlineData("v1.4.0", true)]
    [InlineData("v1.4.1", true)]
    public void 只有更高的正式版本才算新版本(string tag, bool expected)
    {
        var version = tag.TrimStart('v');
        var json = ReleaseJson(tag, $"GlassDesk-Plus-{version}-win-x64-Setup.exe");
        var release = UpdateClient.ParseRelease(json, new Version(1, 4, 0), Source);
        Assert.Equal(expected, release is not null);
    }

    [Theory]
    [InlineData("v1.4")]
    [InlineData("v1.4.0-beta")]
    [InlineData("v1.4.0.1")]
    public void 非三段式稳定版本号会被拒(string tag)
    {
        var json = ReleaseJson(tag, "GlassDesk-Plus-1.4.0-win-x64-Setup.exe");
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source));
    }

    [Fact]
    public void 预发布与草稿不参与更新()
    {
        foreach (var flag in new[] { "draft", "prerelease" })
        {
            var json = ReleaseJson("v1.4.0", "GlassDesk-Plus-1.4.0-win-x64-Setup.exe")
                .Replace("\"draft\": false", flag == "draft" ? "\"draft\": true" : "\"draft\": false", StringComparison.Ordinal)
                .Replace("\"prerelease\": false", flag == "prerelease" ? "\"prerelease\": true" : "\"prerelease\": false", StringComparison.Ordinal);
            Assert.Null(UpdateClient.ParseRelease(json, new Version(1, 0, 4), Source));
        }
    }

    [Theory]
    [InlineData("https://api.github.com/repos/Li-Jiayang85/GlassDesk-Plus/releases/latest")]
    [InlineData("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest")]
    public void 合规的Releases接口地址被接受(string source)
    {
        // 只校验地址形状，不限制所有者：更新源指向哪个仓库由配置决定。
        using var http = new HttpClient();
        var client = new UpdateClient(http, new Uri(source));
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/a/b/releases/latest")]          // 非 https
    [InlineData("https://example.com/repos/a/b/releases/latest")]            // 非 GitHub
    [InlineData("https://api.github.com/repos/a/b/tags")]                    // 不是 releases
    [InlineData("https://api.github.com/repos/a/b/releases/latest?x=1")]     // 带查询串
    public void 不合规的更新源地址被拒(string source)
    {
        using var http = new HttpClient();
        Assert.Throws<InvalidDataException>(() => new UpdateClient(http, new Uri(source)));
    }
}
