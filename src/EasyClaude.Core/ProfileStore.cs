using System.Text;

namespace EasyClaude.Core;

/// <summary>
/// Stores a validated, portable UTF-8 YAML copy. The original file is never modified.
/// </summary>
public sealed class ProfileStore(string file)
{
    public bool Exists => File.Exists(file);

    public async Task<ProfileSummary> ImportAsync(string sourcePath,
        Func<string, CancellationToken, Task> validate, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var yaml = Decode(bytes);
            var summary = RuntimeConfigBuilder.Inspect(yaml);
            await validate(yaml, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(tmp, yaml, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, file, overwrite: true);
            return summary;
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public string LoadText()
    {
        if (!Exists) throw new FileNotFoundException("尚未保存节点配置，请点击“导入节点配置”选择 YAML 文件。", file);
        return Decode(File.ReadAllBytes(file));
    }

    private static string Decode(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
}
