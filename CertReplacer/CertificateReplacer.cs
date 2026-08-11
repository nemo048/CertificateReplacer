using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace CertReplacer;

public sealed class ReplaceOptions
{
    public required string RootDirectory { get; init; }
    public required string NewCertificatePath { get; init; }
    public string[] CertificatePatterns { get; init; } = { "*.pfx", "*.p12", "*.cer*", "*.crt", "*.pem" };
    public string[] ExcludeFolders { get; init; } = Array.Empty<string>();
    public bool IncludeRoot { get; init; }
    public bool DryRun { get; init; }

    /// <summary>Base "backup" folder (typically next to the exe). Null/empty disables backup.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>Overrides the yyyyMMddHHmmss backup session folder name; null uses the current time. Exposed for tests.</summary>
    public string? BackupTimestamp { get; init; }
}

public sealed record ReplaceResult(int Processed, int Skipped, string? BackupDirectory = null);

public enum LogKind
{
    Info,
    Removed,
    Installed,
    Skipped,
    Done,
    Error,
    BackedUp
}

/// <summary>
/// Port of the PowerShell Replace-CertificatesInSubfolders function: for every
/// subfolder under RootDirectory that already contains files matching
/// CertificatePatterns, remove those files and install a copy of the new
/// certificate (keeping its original file name). Folders with no existing
/// certificates are left untouched. ExcludeFolders supports folder names,
/// relative/absolute paths, and wildcard masks, matching the original script.
/// When BackupDirectory is set, every certificate file that is about to be
/// removed or overwritten is copied first to
/// {BackupDirectory}/{yyyyMMddHHmmss}/{parent folder name}/{root folder name}/{relative path}.
/// </summary>
public static class CertificateReplacer
{
    public static ReplaceResult Run(ReplaceOptions options, Action<LogKind, string> log, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RootDirectory).TrimEnd('\\', '/');
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Root directory not found: {options.RootDirectory}");

        var newCertificateFullPath = Path.GetFullPath(options.NewCertificatePath);
        if (!File.Exists(newCertificateFullPath))
            throw new FileNotFoundException("Certificate file not found", options.NewCertificatePath);

        var newCertificateName = Path.GetFileName(newCertificateFullPath);

        string? backupSessionDir = null;
        if (!options.DryRun && !string.IsNullOrWhiteSpace(options.BackupDirectory))
        {
            var timestamp = options.BackupTimestamp ?? DateTime.Now.ToString("yyyyMMddHHmmss");
            backupSessionDir = Path.Combine(options.BackupDirectory, timestamp, GetBackupRootSegment(root));
        }

        var allFolders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(f => !IsFolderExcluded(f, root, options.ExcludeFolders))
            .ToList();

        if (options.IncludeRoot)
        {
            allFolders.Insert(0, root);
        }

        var processed = 0;
        var skipped = 0;
        var prefix = options.DryRun ? "[Dry run] " : string.Empty;

        foreach (var folder in allFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.Combine(folder, newCertificateName);

            var existingCertificates = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(f, newCertificateFullPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => options.CertificatePatterns.Any(p => IsWildcardMatch(Path.GetFileName(f), p)))
                .ToList();

            if (existingCertificates.Count == 0)
            {
                skipped++;
                log(LogKind.Skipped, $"Skip (no certificates): {folder}");
                continue;
            }

            processed++;
            var destinationBackedUp = false;

            foreach (var existing in existingCertificates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isDestination = string.Equals(existing, destinationPath, StringComparison.OrdinalIgnoreCase);

                if (options.DryRun)
                {
                    // The target file itself isn't removed; Copy below overwrites it.
                    if (!isDestination)
                        log(LogKind.Removed, $"{prefix}Would remove: {existing}");
                    continue;
                }

                BackupFile(existing, root, backupSessionDir, log);

                if (isDestination)
                {
                    destinationBackedUp = true;
                    continue;
                }

                ClearReadOnly(existing);
                File.Delete(existing);
                log(LogKind.Removed, $"Removed: {existing}");
            }

            if (options.DryRun)
            {
                log(LogKind.Installed, $"{prefix}Would install: {destinationPath}");
                continue;
            }

            if (!destinationBackedUp)
            {
                BackupFile(destinationPath, root, backupSessionDir, log);
            }

            ClearReadOnly(destinationPath);
            File.Copy(newCertificateFullPath, destinationPath, overwrite: true);
            log(LogKind.Installed, $"Installed: {destinationPath}");
        }

        log(LogKind.Done, $"Done. Processed: {processed}, skipped (no certs): {skipped}");
        return new ReplaceResult(processed, skipped, backupSessionDir);
    }

    /// <summary>
    /// Last two path components of root (e.g. "F:\bin\PFR\091" -> "PFR\091"), so backups from
    /// differently-numbered sibling folders don't collide under a single-segment name like "091".
    /// Falls back to fewer segments for shallow/drive-root paths.
    /// </summary>
    internal static string GetBackupRootSegment(string root)
    {
        var rootName = Path.GetFileName(root);
        if (string.IsNullOrEmpty(rootName)) return "root"; // e.g. root is a bare drive like "C:\"

        var parentDir = Path.GetDirectoryName(root);
        var parentName = string.IsNullOrEmpty(parentDir) ? null : Path.GetFileName(parentDir);

        return string.IsNullOrEmpty(parentName) ? rootName : Path.Combine(parentName, rootName);
    }

    private static void BackupFile(string filePath, string root, string? backupSessionDir, Action<LogKind, string> log)
    {
        if (backupSessionDir == null) return;
        if (!File.Exists(filePath)) return;

        var relativePath = Path.GetRelativePath(root, filePath);
        var backupPath = Path.Combine(backupSessionDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(filePath, backupPath, overwrite: true);
        log(LogKind.BackedUp, $"Backed up: {filePath} -> {backupPath}");
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return path.Replace('/', '\\').TrimEnd('\\');
    }

    internal static bool IsFolderExcluded(string folderFullName, string rootPath, IReadOnlyList<string> excludePatterns)
    {
        if (excludePatterns == null || excludePatterns.Count == 0)
            return false;

        var folderFull = NormalizePath(folderFullName);
        var rootFull = NormalizePath(rootPath);

        if (string.Equals(folderFull, rootFull, StringComparison.OrdinalIgnoreCase))
            return false;

        string? relative = null;
        if (folderFull.Length > rootFull.Length &&
            folderFull.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
        {
            relative = folderFull.Substring(rootFull.Length + 1);
        }

        var folderName = Path.GetFileName(folderFull);

        foreach (var rawPattern in excludePatterns)
        {
            if (string.IsNullOrWhiteSpace(rawPattern)) continue;

            var pattern = NormalizePath(rawPattern);

            if (IsWildcardMatch(folderName, pattern))
                return true;

            if (relative != null)
            {
                if (IsWildcardMatch(relative, pattern))
                    return true;

                if (pattern.EndsWith("\\*"))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 2);
                    if (relative.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(pattern + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (IsWildcardMatch(folderFull, pattern))
                return true;

            if (pattern.EndsWith("\\*"))
            {
                var prefixPath = NormalizePath(pattern.Substring(0, pattern.Length - 2));
                if (folderFull.Equals(prefixPath, StringComparison.OrdinalIgnoreCase) ||
                    folderFull.StartsWith(prefixPath + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (folderFull.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                folderFull.StartsWith(pattern + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Mimics PowerShell's -like operator: * and ? wildcards, case-insensitive.</summary>
    internal static bool IsWildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        var regexPattern = "^" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString())
        })) + "$";

        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
