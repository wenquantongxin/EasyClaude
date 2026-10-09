using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyClaude.Core;

namespace EasyClaude;

/// <summary>Isolated acceptance harness exercising the same restart/exit paths as the window and tray.</summary>
internal static class LifecycleCheck
{
    private sealed record Before(int AppPid, int[] Children, string ProfileHash);
    internal static async Task RunAsync(App app, Orchestrator orchestrator, MainWindow window, AppPaths paths, string result, bool restarted)
    {
        if (orchestrator.OpenCodeClient is not { } client || orchestrator.ChildProcessIds.Count != 2)
            throw new InvalidOperationException("连接服务未启动");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var browser = await window.CheckLocalBrowserAsync(deadline.Token);
        if (!browser) throw new InvalidOperationException("WebView2 本机界面认证或资源加载未成功");
        window.Hide();
        if (!await client.PingAsync(deadline.Token)) throw new InvalidOperationException("隐藏窗口后后台服务不可用");
        var profileHash = await DependencyManager.HashFileAsync(paths.ProfileFile, "sha256", deadline.Token);
        var beforeFile = result + ".before";
        if (!restarted)
        {
            await File.WriteAllTextAsync(beforeFile, JsonSerializer.Serialize(new Before(Environment.ProcessId, orchestrator.ChildProcessIds.ToArray(), profileHash)), deadline.Token);
            await app.RestartProgramAsync();
            return;
        }
        var before = JsonSerializer.Deserialize<Before>(await File.ReadAllTextAsync(beforeFile, deadline.Token))!;
        var currentChildren = orchestrator.ChildProcessIds.ToArray();
        var differentApp = before.AppPid != Environment.ProcessId && !Alive(before.AppPid);
        var oldChildrenGone = before.Children.All(pid => !Alive(pid));
        var profilePreserved = before.ProfileHash == profileHash;
        app.AfterExit = () =>
        {
            var stopped = currentChildren.All(pid => !Alive(pid));
            File.WriteAllText(result, JsonSerializer.Serialize(new
            {
                complete = true,
                ok = differentApp && oldChildrenGone && profilePreserved && stopped,
                mainProcessReplaced = differentApp,
                oldChildrenStopped = oldChildrenGone,
                newChildrenStoppedOnExit = stopped,
                importedProfilePreserved = profilePreserved,
                localWebViewAuthenticated = browser,
                backgroundServicesAlive = true,
                previousAppPid = before.AppPid,
                currentAppPid = Environment.ProcessId,
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Delete(beforeFile);
        };
        await app.ExitAsync();
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
