# Third-party components bundled in the runtime folder

Easy Claude starts mihomo and OpenCode as separate executables. Their names and
licenses remain those of the upstream projects. The binaries have not been
modified; local executable filenames are changed for process isolation.

| Component | Version | Upstream / license |
|---|---|---|
| mihomo | 1.19.32 | https://github.com/MetaCubeX/mihomo — GPL-3.0; see mihomo-LICENSE.txt |
| OpenCode | 2.0.26 | https://github.com/anomalyco/opencode — MIT; see opencode-LICENSE.txt |
| .NET Core / Windows Desktop runtime | See ../EasyClaude.runtimeconfig.json | https://github.com/dotnet/runtime / https://github.com/dotnet/wpf / https://github.com/dotnet/winforms — see bundled license/notice files |
| WebView2 SDK | 1.0.2903.40 | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40 — see webview2-LICENSE.html |
| YamlDotNet | 16.3.0 | https://github.com/aaubry/YamlDotNet — MIT; see yamldotnet-LICENSE.txt |
| Geo databases | Local snapshot | Copied from the existing Clash Verge installation; see ../bin/manifest.json |

mihomo corresponding source and upstream build instructions:
https://github.com/MetaCubeX/mihomo/tree/v1.19.32
https://github.com/MetaCubeX/mihomo/archive/refs/tags/v1.19.32.tar.gz

OpenCode corresponding source:
https://github.com/anomalyco/opencode/tree/v2.0.26

Geo data upstream project and build instructions:
https://github.com/MetaCubeX/meta-rules-dat

The Geo files retain their original local hashes. This packaging run does not
assert that they match a newly fetched upstream release. GeoIP/GeoSite data
contains material from multiple sources; see the upstream project's attribution
and applicable database terms before distributing the bundle beyond local use.
The bundled source links and third-party notices do not grant a new license for
the Easy Claude application itself.
