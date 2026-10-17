using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using GlassDesk.AppServices.Updates;
using GlassDesk.Core.Models;

namespace GlassDesk.UpdateAcceptance;

/// <summary>
/// 更新通道与启动台批量删除的验收。默认只跑不需要网络的部分；
/// 传 <c>--online</c> 再打真实 GitHub，验证「能发现 + 能下载 + 摘要对得上」。
/// </summary>
/// <remarks>
/// 这个工程存在的理由是一次真实交付事故：<c>ParseRelease</c> 认定资产名是
/// <c>GlassDesk-Plus-&lt;版本&gt;-win-x64-Setup.exe</c>，而下载前的 <c>ValidateInstaller</c>
/// 要求 <c>GlassDesk-&lt;版本&gt;-win-x64-Setup.exe</c>（漏了 Plus）。发现新版本那步正常，
/// 一点下载就报「安装包信息无效」。当时的验收只覆盖到发现新版本，所以漏了过去。
/// 现在的断言把两处钉在一起，离线即可发现该类不一致。
/// </remarks>
internal static partial class Program
{
    private static readonly List<object> Cases = [];
    private static int _failed;

    private const string Repository = "Li-Jiayang85/GlassDesk-Plus";
    private static readonly Uri Source = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly Uri UpstreamSource = new("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest");

    private const string Digest = "sha256:6bda132f4aeea26fecbc9c9b79a0b4672858ad3b297ee2e894409c81879d376c";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("== 离线：更新信息解析与安装包校验 ==");
            VerifyParsingOffline();
            VerifyBatchRemovalCore();

            if (args.Contains("--online"))
            {
                Console.WriteLine();
                Console.WriteLine("== 在线：打真实 GitHub ==");
                VerifyOnlineAsync(args).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new { name = "runner", ids = "U-01", driver = "acceptance", expected = "no exception", actual = ex.ToString(), status = "Failed" });
        }

        var report = Argument(args, "--report");
        if (report is not null)
        {
            var path = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                online = args.Contains("--online"),
                repository = Repository,
                passed = Cases.Count - _failed,
                failed = _failed,
                cases = Cases,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Console.WriteLine();
        Console.WriteLine($"更新与批量删除验收：{Cases.Count - _failed} 通过，{_failed} 失败。"
            + (report is null ? string.Empty : $" 报告：{Path.GetFullPath(report)}"));
        return _failed == 0 ? 0 : 1;
    }

    // ---- 离线：更新通道 ----

    private static void VerifyParsingOffline()
    {
        // 1) 带 Plus 的资产名：解析要成功，校验也要通过。这一条就是那个事故的回归守卫。
        var releaseJson = ReleaseJson("v1.4.1", $"GlassDesk-Plus-1.4.1-win-x64-Setup.exe");
        var parsed = UpdateClient.ParseRelease(releaseJson, new Version(1, 0, 4), Source);
        Check("U-01", "带 Plus 的资产名可解析且通过安装包校验",
            parsed is not null && parsed.FileName == "GlassDesk-Plus-1.4.1-win-x64-Setup.exe",
            "解析成功且文件名一致",
            $"文件名={parsed?.FileName ?? "null"}",
            () => UpdateClient.EnsureUsable(parsed!));

        // 2) 不带 Plus 的名字必须被拒：解析阶段就拦下，而不是等到下载前。
        Check("U-02", "不带 Plus 的资产名不会被解析",
            Throws<InvalidDataException>(() => UpdateClient.ParseRelease(
                ReleaseJson("v1.4.1", "GlassDesk-1.4.1-win-x64-Setup.exe"), new Version(1, 0, 4), Source)),
            "抛 InvalidDataException", "已按预期拒绝", null);

        // 3) 两处期望必须是同一个字符串——任何一边改名而另一边没跟上，这里就红。
        var mismatched = new UpdateRelease(new Version(1, 4, 1), "GlassDesk-1.4.1-win-x64-Setup.exe",
            new Uri($"https://github.com/{Repository}/releases/download/v1.4.1/GlassDesk-1.4.1-win-x64-Setup.exe"),
            Digest[7..], 53950502, "notes", new Uri($"https://github.com/{Repository}/releases/tag/v1.4.1"));
        Check("U-03", "校验会拒绝与解析命名约定不符的安装包",
            Throws<InvalidDataException>(() => UpdateClient.EnsureUsable(mismatched)),
            "抛 InvalidDataException（本分支只认带 Plus 的名字）", "已按预期拒绝", null);

        // 4) 版本比较：只有“严格更高”的正式版本才算新版本（相等即已是最新）。
        var versions = new (string Tag, bool Expected)[]
        {
            ("v1.0.4", false), ("v1.0.5", false), ("v1.3.1", false),
            ("v1.3.6", false), ("v1.4.0", false), ("v1.4.1", false), ("v1.5.0", true),
        };
        var mismatches = new List<string>();
        foreach (var (tag, expected) in versions)
        {
            var text = tag[1..];
            var release = UpdateClient.ParseRelease(
                ReleaseJson(tag, $"GlassDesk-Plus-{text}-win-x64-Setup.exe"), new Version(1, 4, 1), Source);
            if ((release is not null) != expected)
                mismatches.Add($"{tag} 期望{(expected ? "有更新" : "无更新")} 实际{(release is null ? "无更新" : release.Version.ToString())}");
        }
        Check("U-04", "相对 1.4.1 逐版本判定是否有更新", mismatches.Count == 0,
            "≤1.4.1 无更新；≥1.5.0 有更新",
            mismatches.Count == 0 ? "全部符合" : string.Join("；", mismatches), null);

        // 5) 只接受三段式稳定版本号。
        var badTags = new[] { "v1.4", "v1.4.1-beta", "v1.4.1.1" };
        Check("U-05", "非三段式稳定版本号被拒", badTags.All(tag => Throws<InvalidDataException>(() =>
                UpdateClient.ParseRelease(ReleaseJson(tag, "GlassDesk-Plus-1.4.1-win-x64-Setup.exe"), new Version(1, 0, 4), Source))),
            "全部拒绝", "已按预期拒绝", null);

        // 6) 缺 digest 的资产不能用：更新包必须可校验。
        Check("U-06", "缺少 sha256 digest 的资产被拒",
            Throws<InvalidDataException>(() => UpdateClient.ParseRelease(
                ReleaseJson("v1.4.1", "GlassDesk-Plus-1.4.1-win-x64-Setup.exe", digest: ""), new Version(1, 0, 4), Source)),
            "抛 InvalidDataException", "已按预期拒绝", null);

        // 7) draft / prerelease 不参与更新。
        var draftIgnored = UpdateClient.ParseRelease(
            ReleaseJson("v1.4.1", "GlassDesk-Plus-1.4.1-win-x64-Setup.exe", draft: true), new Version(1, 0, 4), Source) is null;
        var preIgnored = UpdateClient.ParseRelease(
            ReleaseJson("v1.4.1", "GlassDesk-Plus-1.4.1-win-x64-Setup.exe", prerelease: true), new Version(1, 0, 4), Source) is null;
        Check("U-07", "draft 与 prerelease 都不参与更新", draftIgnored && preIgnored,
            "两者都返回无更新", $"draft={draftIgnored} prerelease={preIgnored}", null);

        // 8) 更新源地址必须是 GitHub 的 releases 接口形状。
        var badSources = new[]
        {
            "http://api.github.com/repos/a/b/releases/latest",
            "https://example.com/repos/a/b/releases/latest",
            "https://api.github.com/repos/a/b/tags",
            "https://api.github.com/repos/a/b/releases/latest?x=1",
        };
        Check("U-08", "不合规的更新源地址被拒",
            badSources.All(source => Throws<InvalidDataException>(() => new UpdateClient(new HttpClient(), new Uri(source)))),
            "全部拒绝", "已按预期拒绝", null);

        // 9) 指向上游作者仓库的地址形状合法（只校验形状，不限制所有者），
        //    但那里的资产命名与本分支不一致，所以会以「没有唯一的安装包」失败——
        //    这正是旧版报错的来源，锁住它以区分「配置错」与「发布错」。
        Check("U-09", "上游更新源的地址形状被接受（该报错来自配置而非发布）",
            !Throws<InvalidDataException>(() => new UpdateClient(new HttpClient(), UpstreamSource)),
            "构造函数不抛错", "已接受", null);
    }

    // ---- 离线：启动台批量删除核心 ----

    private static void VerifyBatchRemovalCore()
    {
        static LauncherLayout Layout(params string[] ids) => LauncherLayoutRules.Reconcile(new LauncherLayout(), ids, 35);

        var initial = Layout("a", "b", "c", "d");
        var removed = LauncherLayoutEditor.RemoveShortcuts(initial, ["a", "c"], 35);
        Check("U-10", "批量移除只删指定项且保留其余",
            LauncherLayoutRules.AppIds(removed).SequenceEqual(["b", "d"]) && removed.ExcludedAppIds.Count == 0
            && LauncherLayoutRules.AppIds(LauncherLayoutRules.Reconcile(removed, ["a", "b", "c", "d"], 35)).SequenceEqual(["b", "d"]),
            "剩 b、d，且不留隐藏名单",
            string.Join(",", LauncherLayoutRules.AppIds(removed)), null);

        var grouped = LauncherLayoutEditor.CreateGroup(Layout("a", "b", "keep"), "a", "b", "group", 35);
        var collapsed = LauncherLayoutEditor.RemoveShortcuts(grouped, ["a"], 35);
        Check("U-11", "分组只剩一个成员时摊回普通图标",
            LauncherLayoutRules.AppIds(collapsed).SequenceEqual(["b", "keep"])
            && !collapsed.Pages.SelectMany(page => page.Slots).Any(slot => slot.Kind == LauncherSlotKind.Group),
            "b、keep 且无分组外壳",
            string.Join(",", LauncherLayoutRules.AppIds(collapsed)), null);

        var unknown = LauncherLayoutEditor.RemoveShortcuts(Layout("a", "b"), ["a", "a", "never-existed", ""], 35);
        Check("U-12", "未知与重复 id 被忽略",
            LauncherLayoutRules.AppIds(unknown).SequenceEqual(["b"]),
            "剩 b", string.Join(",", LauncherLayoutRules.AppIds(unknown)), null);

        var source = Layout("a", "b", "c");
        _ = LauncherLayoutEditor.RemoveShortcuts(source, ["a", "b"], 35);
        Check("U-13", "批量移除不修改来源布局",
            LauncherLayoutRules.AppIds(source).SequenceEqual(["a", "b", "c"]),
            "来源仍为 a、b、c", string.Join(",", LauncherLayoutRules.AppIds(source)), null);

        var emptied = LauncherLayoutEditor.RemoveShortcuts(Layout("a", "b"), ["a", "b"], 35);
        Check("U-14", "清空后仍能被重新协商",
            emptied.Pages.Count == 0 && LauncherLayoutRules.AppIds(LauncherLayoutRules.Reconcile(emptied, ["a", "b"], 35)).Count == 0,
            "空布局且协商后仍为空", $"{emptied.Pages.Count} 页", null);
    }

    // ---- 在线 ----

    private static async Task VerifyOnlineAsync(string[] args)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GlassDesk-Acceptance/1.0");
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var client = new UpdateClient(http, Source);
        var latest = await client.CheckAsync(new Version(0, 0, 1), CancellationToken.None);
        if (latest is null)
        {
            Check("U-20", "线上存在可用的正式版本", false, "至少一个正式 release", "没有取到（可能未限流或没有正式发布）", null);
            return;
        }
        Console.WriteLine($"线上最新正式版本：{latest.Version}");

        // 所有真实存在的安装版本都必须能收到更新。
        foreach (var installed in new[] { new Version(1, 0, 4), new Version(1, 0, 5), new Version(1, 3, 1), new Version(1, 3, 6) })
        {
            var found = await client.CheckAsync(installed, CancellationToken.None);
            Check("U-21", $"已装 {installed} 能发现 {latest.Version}",
                found?.Version == latest.Version,
                $"返回 {latest.Version}", found is null ? "null（收不到更新）" : found.Version.ToString(), null);
        }

        var sameVersion = await client.CheckAsync(latest.Version, CancellationToken.None);
        Check("U-22", $"已装 {latest.Version} 判为已是最新",
            sameVersion is null, "返回 null", sameVersion is null ? "null" : sameVersion.Version.ToString(), null);

        // 端到端：真下载并核对 sha256。这一条才是能抓住「安装包信息无效」的测试。
        // 缓存目录默认落在 exe 旁边而不是 %TEMP%：受限会话下 %TEMP% 可能不可写，
        // 那属于环境限制，不该让验收以「下载失败」的形式报出来。
        var cacheRoot = Argument(args, "--cache-root")
            ?? Path.Combine(AppContext.BaseDirectory, "update-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(cacheRoot);
            var progressCount = 0;
            var path = await client.DownloadAsync(latest, cacheRoot, new Progress<int>(_ => progressCount++), CancellationToken.None);
            var info = new FileInfo(path);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Check("U-23", "真实下载安装包并通过 sha256 校验",
                info.Length == latest.Size && actual == latest.Sha256,
                $"{latest.Size} 字节且摘要一致",
                $"{info.Length} 字节，摘要一致={actual == latest.Sha256}，进度回调 {progressCount} 次", null);
        }
        catch (Exception ex)
        {
            Check("U-23", "真实下载安装包并通过 sha256 校验", false, "下载成功且摘要一致", ex.Message, null);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(cacheRoot, recursive: true); } catch (IOException) { }
        }

        // 上游旧源：形状合法但资产命名不匹配，必须报「没有唯一的安装包」。
        // 这条把「配置错」钉死，避免以后再把它误判成发布有问题。
        try
        {
            var upstream = await new UpdateClient(http, UpstreamSource).CheckAsync(new Version(1, 0, 4), CancellationToken.None);
            Check("U-24", "上游旧源以「没有唯一的安装包」失败",
                false, "抛 InvalidDataException", upstream is null ? "返回 null" : upstream.Version.ToString(), null);
        }
        catch (InvalidDataException ex)
        {
            Check("U-24", "上游旧源以「没有唯一的安装包」失败",
                ex.Message.Contains("安装包"), "消息含「安装包」", ex.Message, null);
        }
    }

    // ---- 支撑 ----

    private static string ReleaseJson(string tag, string assetName, string digest = Digest,
        bool draft = false, bool prerelease = false)
    {
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/{{Repository}}/releases/tag/{{tag}}",
          "body": "notes",
          "assets": [
            {
              "name": "{{assetName}}",
              "state": "uploaded",
              "size": 53950502,
              "digest": "{{digest}}",
              "browser_download_url": "https://github.com/{{Repository}}/releases/download/{{tag}}/{{assetName}}"
            }
          ]
        }
        """;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    private static void Check(string ids, string name, bool passed, string expected, string actual, Action? alsoExpectThrow)
    {
        if (passed && alsoExpectThrow is not null)
        {
            try { alsoExpectThrow(); }
            catch (Exception ex)
            {
                passed = false;
                actual += $"；附加断言抛 {ex.GetType().Name}: {ex.Message}";
            }
        }
        if (!passed) _failed++;
        Cases.Add(new { name, ids, driver = "production code, no UI", expected, actual, status = passed ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}");
        if (!passed) Console.WriteLine($"      {actual}");
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}


