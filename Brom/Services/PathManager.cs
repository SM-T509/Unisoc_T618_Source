using System;
using System.IO;

namespace Brom.Services;

public sealed class PathManager
{
    private static readonly Lazy<PathManager> _instance = new(() => new PathManager());
    public static PathManager Instance => _instance.Value;

    private readonly string _baseDirectory;
    private readonly string _projectRoot;

    private PathManager()
    {
        _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Optional override for portable deployments:
        //     setx BROM_ROOT "D:\Tools\Brom"
        string envRoot = null;
        try { envRoot = Environment.GetEnvironmentVariable("BROM_ROOT"); } catch { }

        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            try
            {
                string candidate = Path.GetFullPath(envRoot.Trim().Trim('"'));
                if (Directory.Exists(candidate))
                {
                    _projectRoot = candidate;
                    return;
                }
            }
            catch
            {
                // Bad value: fall through to normal resolution.
            }
        }

        _projectRoot = ResolveProjectRoot(_baseDirectory);
    }

    public string BaseDirectory => _baseDirectory;
    public string ProjectRoot => _projectRoot;

    public string GetBinariesPath() => Combine("bins");
    public string GetLoadersPath() => Combine("loaders");
    public string GetSdksPath() => Combine("sdks");
    public string GetBackupPath() => Combine("backup");

    public string GetAdbPath() => Path.Combine(GetSdksPath(), "platform-tools", "adb.exe");
    public string GetT509BinsPath() => Combine("bins", "t509");

    // Single source of truth for every BROM/FDL/handshake file:
    //     <root>\loaders\T509
    public string GetBromLoadersPath() => Combine("loaders", "T509");
    public string GetFdl1Path() => Path.Combine(GetBromLoadersPath(), "fdl1-dl.bin");
    public string GetFdl2Path() => Path.Combine(GetBromLoadersPath(), "fdl2.bin");
    public string GetFdl2DlPath() => Path.Combine(GetBromLoadersPath(), "fdl2-dl.bin");
    public string GetStubPath() => Path.Combine(GetBromLoadersPath(), "custom_exec_no_verify_3ee8.bin");
    public string GetFixPath() => Path.Combine(GetBromLoadersPath(), "T509_Fix.bin");

    // Backwards-compatible aliases used by the T509 engine.
    public string GetT509LoadersPath() => GetBromLoadersPath();
    public string GetT509Fdl1Path() => GetFdl1Path();
    public string GetT509Fdl2Path() => GetFdl2Path();
    public string GetT509Fdl2DlPath() => GetFdl2DlPath();
    public string GetT509UbootPath() => Path.Combine(GetT509LoadersPath(), "uboot.bin");
    public string GetT509FixPath() => GetFixPath();
    public string GetT509FixBinPath() => Path.Combine(GetT509BinsPath(), "T509_Fix.bin");
    public string GetT509CustomExecPath() => GetStubPath();

    public string GetTempPath() => Path.Combine(Path.GetTempPath(), "Brom");

    public string ResolvePath(params string[] relativeParts)
    {
        string candidate = Path.Combine(_projectRoot, Path.Combine(relativeParts));
        if (File.Exists(candidate) || Directory.Exists(candidate))
            return candidate;

        candidate = Path.Combine(_baseDirectory, Path.Combine(relativeParts));
        if (File.Exists(candidate) || Directory.Exists(candidate))
            return candidate;

        return Path.Combine(_projectRoot, Path.Combine(relativeParts));
    }

    public string FindFile(string fileName, params string[] searchRoots)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            return fileName;

        string lookupName = Path.IsPathRooted(fileName) ? Path.GetFileName(fileName) : fileName;
        if (string.IsNullOrEmpty(lookupName))
            return null;

        foreach (string root in searchRoots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string fullPath = Path.Combine(root, lookupName);
            if (File.Exists(fullPath))
                return fullPath;
        }

        foreach (string root in new[] { _projectRoot, _baseDirectory, GetLoadersPath(), GetSdksPath(), GetBinariesPath() })
        {
            string fullPath = Path.Combine(root, lookupName);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    public string FindFirstExisting(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string expanded = candidate;
            if (!Path.IsPathRooted(candidate))
            {
                expanded = ResolvePath(candidate);
            }

            if (File.Exists(expanded) || Directory.Exists(expanded))
                return expanded;
        }
        return null;
    }

    public void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }
        catch { }
    }

    private static string ResolveProjectRoot(string startPath)
    {
        string repoRoot = FindRepoRoot(startPath);
        if (!string.IsNullOrEmpty(repoRoot))
            return repoRoot;

        string current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (LooksLikeToolRoot(current))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return startPath;
    }

    private static string FindRepoRoot(string startPath)
    {
        string current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Brom.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            if (LooksLikeBuildOutput(current))
            {
                current = Path.GetDirectoryName(current);
                continue;
            }

            current = Path.GetDirectoryName(current);
        }
        return string.Empty;
    }

    private static bool LooksLikeBuildOutput(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return false;
        string normalized = dir.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.EndsWith(Path.DirectorySeparatorChar + "bin", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.EndsWith(Path.DirectorySeparatorChar + "obj", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.EndsWith(Path.DirectorySeparatorChar + "publish", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf(Path.DirectorySeparatorChar + "publish" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeToolRoot(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        return Directory.Exists(Path.Combine(directory, "sdks"))
            || Directory.Exists(Path.Combine(directory, "loaders"))
            || Directory.Exists(Path.Combine(directory, "bins"))
            || File.Exists(Path.Combine(directory, "Brom.sln"));
    }

    private string Combine(params string[] relative) => Path.Combine(_projectRoot, Path.Combine(relative));
}
