using System.Security.Cryptography;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

internal static class BenchmarkArtifacts
{
    internal static IReadOnlyDictionary<string, string> Hashes(string fixturePath) => new Dictionary<string, string>
    {
        ["desktop_sha256"] = Hash(typeof(DesktopEngine).Assembly.Location),
        ["core_sha256"] = Hash(typeof(IDesktopEngine).Assembly.Location),
        ["safety_sha256"] = Hash(typeof(SafetyGate).Assembly.Location),
        ["harness_sha256"] = Hash(typeof(BenchmarkArtifacts).Assembly.Location),
        ["fixture_sha256"] = Hash(Path.ChangeExtension(fixturePath, ".dll")),
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
