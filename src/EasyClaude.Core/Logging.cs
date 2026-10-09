using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyClaude.Core;

/// <summary>Removes credentials, node addresses and tokens from anything that is logged or shown.</summary>
public sealed class Redactor
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex UrlUserInfo = new(@"(?i)\b([a-z][a-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled);
    private static readonly Regex AnthropicKey = new(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    private static readonly Regex AuthHeader = new(@"(?i)(authorization:\s*(?:basic|bearer)\s+)\S+", RegexOptions.Compiled);
    private static readonly Regex Password = new("(?i)(password[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s\\\"',}]+", RegexOptions.Compiled);
    private static readonly Regex PublicIpv4 = new(@"\b(?!127\.0\.0\.1\b)(?!0\.0\.0\.0\b)(?!192\.168\.)\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"\b[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    public void AddSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 6) _secrets.TryAdd(secret, 0);
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = Ansi.Replace(input, string.Empty);
        foreach (var secret in _secrets.Keys) s = s.Replace(secret, "[secret]", StringComparison.Ordinal);
        s = UrlUserInfo.Replace(s, "$1[secret]@");
        s = AnthropicKey.Replace(s, "[key]");
        s = AuthHeader.Replace(s, "$1[secret]");
        s = Password.Replace(s, "$1[secret]");
        s = PublicIpv4.Replace(s, "[ip]");
        s = LongToken.Replace(s, "[token]");
        return s;
    }
}

/// <summary>Thread-safe, redacting, size-capped log file.</summary>
public sealed class FileLogger : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Redactor _redactor;
    private StreamWriter? _writer;

    public FileLogger(string path, Redactor redactor)
    {
        _path = path;
        _redactor = redactor;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = Open();
    }

    /// <summary>Optional mirror of every (already redacted) line, e.g. the console in the smoke tool.</summary>
    public Action<string>? Echo { get; set; }

    public void Write(string source, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {_redactor.Redact(message)}";
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                if (_writer.BaseStream.Length > MaxBytes) Rotate();
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
        try { Echo?.Invoke(line); }
        catch
        {
            // never let a log mirror break the caller
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

    private void Rotate()
    {
        _writer!.Dispose();
        var old = _path + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(_path, old);
        _writer = Open();
    }
}
