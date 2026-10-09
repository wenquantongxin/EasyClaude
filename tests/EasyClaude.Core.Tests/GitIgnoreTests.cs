using System.Diagnostics;

namespace EasyClaude.Core.Tests;

public class GitIgnoreTests
{
    [Fact]
    public async Task Git_parser_excludes_private_runtime_and_keeps_icons_manifest_and_safe_examples()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var root = EasyClaude.Core.AppPaths.FindRoot(AppContext.BaseDirectory);
            File.Copy(Path.Combine(root, ".gitignore"), Path.Combine(dir, ".gitignore"));
            async Task<int> Git(params string[] args)
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(), process.WaitForExitAsync());
                return process.ExitCode;
            }
            Assert.Equal(0, await Git("init", "--quiet"));
            foreach (var name in new[] { "data/opencode/session.db", "bin/easyclaude-mihomo.exe", "app/EasyClaude.exe", "src/EasyClaude/bin/test.dll", "node.yml", ".env", "copy.dpapi" })
                Assert.Equal(0, await Git("check-ignore", "--no-index", "--quiet", name));
            foreach (var name in new[] { "src/EasyClaude/Assets/EasyClaude.ico", "src/EasyClaude.Core/dependencies.json", "examples/node.example.yml", ".github/workflows/build.yml", "README.md" })
                Assert.Equal(1, await Git("check-ignore", "--no-index", "--quiet", name));
        }
        finally { Directory.Delete(dir, true); }
    }
}
