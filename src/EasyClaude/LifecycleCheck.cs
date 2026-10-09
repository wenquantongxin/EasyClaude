using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EasyClaude.Core;

namespace EasyClaude;

/// <summary>Isolated acceptance harness exercising the same restart/exit paths as the window and tray.</summary>
internal static class LifecycleCheck
{
    private sealed record Before(int AppPid, int[] Children, string ProfileHash, DateTimeOffset TestedAt);
    internal static async Task RunAsync(App app, Orchestrator orchestrator, MainWindow window, AppPaths paths, string result, bool restarted)
    {
        if (orchestrator.OpenCodeClient is not { } client || orchestrator.ChildProcessIds.Count != 2)
            throw new InvalidOperationException("连接服务未启动");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var browser = await window.CheckLocalBrowserAsync(deadline.Token);
        if (!browser) throw new InvalidOperationException("WebView2 本机界面认证或资源加载未成功");
        var web = (Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("Web");
        await web.CoreWebView2.ExecuteScriptAsync("window.__easyClaudeHelpMarker = 'preserved';").WaitAsync(deadline.Token);
        window.ShowHelp();
        var helpWorks = ((ContentControl)window.FindName("HelpPanel")).Visibility == Visibility.Visible &&
            web.Visibility == Visibility.Collapsed && await client.PingAsync(deadline.Token);
        if (!helpWorks) throw new InvalidOperationException("帮助页未显示或浏览帮助时后台服务不可用");
        window.HideHelp();
        var chatPreserved = web.Visibility == Visibility.Visible &&
            await web.CoreWebView2.ExecuteScriptAsync("window.__easyClaudeHelpMarker").WaitAsync(deadline.Token) == "\"preserved\"" &&
            await window.CheckLocalBrowserAsync(deadline.Token);
        if (!chatPreserved) throw new InvalidOperationException("关闭帮助后聊天页面状态或认证未保留");
        window.Hide();
        if (!await client.PingAsync(deadline.Token)) throw new InvalidOperationException("隐藏窗口后后台服务不可用");
        var profileHash = await DependencyManager.HashFileAsync(paths.ProfileFile, "sha256", deadline.Token);
        var beforeFile = result + ".before";
        if (!restarted)
        {
            // Synthetic fixture in the isolated instance: no real Key or model request is involved.
            await client.ReplaceAnthropicKeyAsync("sk-ant-lifecycle-dummy-not-a-real-key", deadline.Token);
            var credential = (await client.ListKeyCredentialsAsync("anthropic", deadline.Token)).Single(c => c.Active);
            var testedAt = DateTimeOffset.UtcNow;
            new ApiKeyVerificationStore(paths.ApiKeyTestFile).Save(credential.Identity!, new(true, "模型测试成功", "claude-haiku-test", LatencyMs: 142), testedAt);
            await File.WriteAllTextAsync(beforeFile, JsonSerializer.Serialize(new Before(Environment.ProcessId, orchestrator.ChildProcessIds.ToArray(), profileHash, testedAt)), deadline.Token);
            await app.RestartProgramAsync();
            return;
        }
        var modelVisibility = await CheckModelVisibilityAsync(client, web, deadline.Token);
        var before = JsonSerializer.Deserialize<Before>(await File.ReadAllTextAsync(beforeFile, deadline.Token))!;
        var currentChildren = orchestrator.ChildProcessIds.ToArray();
        var differentApp = before.AppPid != Environment.ProcessId && !Alive(before.AppPid);
        var oldChildrenGone = before.Children.All(pid => !Alive(pid));
        var profilePreserved = before.ProfileHash == profileHash;
        var keyTestRestored = orchestrator.ApiKey.IsHistorical && orchestrator.ApiKey.LastTest is { Ok: true, Model: "claude-haiku-test", LatencyMs: 142 } && orchestrator.ApiKey.TestedAt == before.TestedAt;
        if (!keyTestRestored) throw new InvalidOperationException("程序重启后未恢复当前凭据的测试记录");
        app.AfterExit = () =>
        {
            var stopped = currentChildren.All(pid => !Alive(pid));
            File.WriteAllText(result, JsonSerializer.Serialize(new
            {
                complete = true,
                ok = differentApp && oldChildrenGone && profilePreserved && stopped && keyTestRestored && modelVisibility,
                mainProcessReplaced = differentApp,
                oldChildrenStopped = oldChildrenGone,
                newChildrenStoppedOnExit = stopped,
                importedProfilePreserved = profilePreserved,
                localWebViewAuthenticated = browser,
                embeddedHelpKeepsBackendAlive = helpWorks,
                chatPagePreservedAfterHelp = chatPreserved,
                rememberedKeyTestRestored = keyTestRestored,
                webCatalogShowsOnlyThreeClaudeFamilies = modelVisibility,
                backendCatalogPreserved = modelVisibility,
                backgroundServicesAlive = true,
                previousAppPid = before.AppPid,
                currentAppPid = Environment.ProcessId,
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Delete(beforeFile);
        };
        await app.ExitAsync();
    }

    private static async Task<bool> CheckModelVisibilityAsync(OpenCodeClient client, Microsoft.Web.WebView2.Wpf.WebView2 web, CancellationToken ct)
    {
        string[]? expected = null;
        var nativeHasHiddenModels = false;
        while (expected is null || expected.Length == 0)
        {
            var (status, body) = await client.GetAsync("api/model", ct);
            if (status == 200)
            {
                using var native = JsonDocument.Parse(body);
                using var visible = JsonDocument.Parse(ModelCatalogVisibility.Filter(body));
                expected = visible.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order().ToArray();
                nativeHasHiddenModels = native.RootElement.GetProperty("data").GetArrayLength() > expected.Length;
            }
            if (expected is null || expected.Length == 0) await Task.Delay(200, ct);
        }
        await web.CoreWebView2.ExecuteScriptAsync("window.__easyClaudeModels = null; fetch('/api/model?easyClaudeCheck=1').then(async r => { const body = await r.json(); window.__easyClaudeModels = { status: r.status, data: body.data }; }).catch(() => window.__easyClaudeModels = { status: -1 });").WaitAsync(ct);
        while (true)
        {
            var json = await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__easyClaudeModels)").WaitAsync(ct);
            var value = JsonSerializer.Deserialize<string>(json);
            if (value is not null && value != "null")
            {
                using var view = JsonDocument.Parse(value);
                if (view.RootElement.GetProperty("status").GetInt32() != 200) throw new InvalidOperationException("网页模型目录请求失败");
                var actual = view.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order().ToArray();
                if (!nativeHasHiddenModels || !expected.SequenceEqual(actual)) throw new InvalidOperationException("网页模型过滤未生效或后台目录被修改");
                return true;
            }
            await Task.Delay(200, ct);
        }
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
