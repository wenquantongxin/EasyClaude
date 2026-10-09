using System.Security.Cryptography;
using System.Text;

namespace EasyClaude.Core;

/// <summary>
/// Stores the imported proxy profile encrypted with DPAPI (CurrentUser scope): only this Windows user on this
/// machine can decrypt it. The original file is never modified.
/// </summary>
public sealed class ProfileStore(string file)
{
    private static readonly byte[] Entropy = "EasyClaude.profile.v1"u8.ToArray();

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
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(tmp, encrypted, ct).ConfigureAwait(false);
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
        if (!Exists) throw new FileNotFoundException("尚未导入代理配置");
        var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
        try { return Decode(decrypted); }
        finally { Array.Clear(decrypted); }
    }

    private static string Decode(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
}
