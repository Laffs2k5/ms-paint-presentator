using System.Text.Json;

namespace Presenter.Slides;

/// <summary>A Node package's command-line entry, found on disk.</summary>
/// <param name="ProjectDir">The folder that holds the <c>node_modules</c> it is in.</param>
/// <param name="PackageDir">The package folder (…/node_modules/&lt;name&gt;).</param>
/// <param name="Script">The JavaScript file its <c>bin</c> names.</param>
/// <param name="Version">Its version, from its package.json.</param>
internal sealed record NodePackage(string ProjectDir, string PackageDir, string Script, string Version);

/// <summary>
/// Finds Node, a deck's own Marp or Slidev, and Edge. Tools run as
/// <c>node &lt;script&gt;</c> rather than through npm's <c>.cmd</c> shims, so
/// arguments never pass through cmd.exe's quoting.
/// </summary>
internal static class NodeTools
{
    /// <summary>Where the presenter keeps its own Node tools when a deck has none:
    /// %LOCALAPPDATA%\PaintPresentator\node-tools.</summary>
    public static string DefaultToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaintPresentator", "node-tools");

    public static string DefaultEdge =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft\Edge\Application\msedge.exe");

    /// <summary>The package's entry in the nearest <c>node_modules</c> at or
    /// above <paramref name="startDir"/> (Node's own lookup), or null.</summary>
    public static NodePackage? FindUpwards(string startDir, string package, string bin)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var pkg = InDir(dir.FullName, package, bin);
            if (pkg is not null) return pkg;
        }
        return null;
    }

    /// <summary>The package in <paramref name="projectDir"/>/node_modules, or null.</summary>
    public static NodePackage? InDir(string projectDir, string package, string bin)
    {
        var dir = Path.Combine(projectDir, "node_modules", package.Replace('/', Path.DirectorySeparatorChar));
        var json = Path.Combine(dir, "package.json");
        if (!File.Exists(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
            string? script = null;
            if (root.TryGetProperty("bin", out var b))
            {
                if (b.ValueKind == JsonValueKind.String) script = b.GetString();
                else if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty(bin, out var s)) script = s.GetString();
            }
            if (script is null) return null;
            var full = Path.GetFullPath(Path.Combine(dir, script));
            return File.Exists(full) ? new NodePackage(Path.GetFullPath(projectDir), dir, full, version) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>node.exe: next to the tools (as npm's shims look first), on PATH, or the installer's default.</summary>
    public static string FindNode(string projectDir)
    {
        var local = Path.Combine(projectDir, "node_modules", ".bin", "node.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (File.Exists(programFiles)) return programFiles;
        throw new InvalidOperationException(
            "Node.js was not found (node.exe on PATH). Install Node.js LTS from https://nodejs.org to render Marp and Slidev decks.");
    }

    public static string FindEdge(string? configured)
    {
        if (configured is not null)
            return File.Exists(configured) ? configured
                : throw new InvalidOperationException($"Edge was not found at the configured path {configured}.");
        if (File.Exists(DefaultEdge)) return DefaultEdge;
        var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Microsoft\Edge\Application\msedge.exe");
        if (File.Exists(alt)) return alt;
        throw new InvalidOperationException($"Microsoft Edge was not found at {DefaultEdge}; set RenderOptions.EdgePath.");
    }
}
