using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CertReplacer;

var failures = new List<string>();

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
}

// --- Wildcard matching ---
Check(CertificateReplacer.IsWildcardMatch("cert.pfx", "*.pfx"), "wildcard *.pfx matches cert.pfx");
Check(!CertificateReplacer.IsWildcardMatch("cert.p12", "*.pfx"), "wildcard *.pfx does not match cert.p12");
Check(CertificateReplacer.IsWildcardMatch("cert.cer.bak", "*.cer*"), "wildcard *.cer* matches cert.cer.bak");
Check(CertificateReplacer.IsWildcardMatch("CERT.PFX", "*.pfx"), "wildcard match is case-insensitive");
Check(CertificateReplacer.IsWildcardMatch("9x", "9*"), "wildcard 9* matches 9x");
Check(CertificateReplacer.IsWildcardMatch("a.b", "a.b"), "wildcard exact match");
Check(CertificateReplacer.IsWildcardMatch("a.b", "a?b"), "wildcard ? matches single char");
Check(!CertificateReplacer.IsWildcardMatch("a.bb", "a?b"), "wildcard ? does not match two chars");

// --- Folder exclusion ---
var root = @"C:\certs";
Check(!CertificateReplacer.IsFolderExcluded(root, root, new[] { "00" }), "root itself is never excluded");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\00", root, new[] { "00" }), "exact folder name excluded");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\00\sub", root, new[] { "00" }), "children of excluded folder name excluded (plain name)");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\00\sub", root, new[] { @"00\*" }), "children of excluded folder excluded (mask 00\\*)");
Check(!CertificateReplacer.IsFolderExcluded(@"C:\certs\001", root, new[] { "00" }), "similarly-prefixed sibling not excluded by exact name");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\90", root, new[] { "9*" }), "wildcard mask 9* excludes folder");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\archive\logs", root, new[] { @"archive\*" }), "relative path mask excludes nested folder");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\00", root, new[] { @"C:\certs\00" }), "absolute path excludes folder");
Check(CertificateReplacer.IsFolderExcluded(@"C:\certs\00", root, new[] { "C:/certs/00" }), "absolute path with forward slashes normalizes and excludes");
Check(!CertificateReplacer.IsFolderExcluded(@"C:\certs\keep", root, Array.Empty<string>()), "no patterns excludes nothing");

// --- End to end Run() ---
string tmp = Path.Combine(Path.GetTempPath(), "certreplacer-test-" + Guid.NewGuid());
Directory.CreateDirectory(tmp);
try
{
    var sub1 = Path.Combine(tmp, "sub1"); Directory.CreateDirectory(sub1);
    var sub2NoCert = Path.Combine(tmp, "sub2-nocert"); Directory.CreateDirectory(sub2NoCert);
    var excluded = Path.Combine(tmp, "excluded"); Directory.CreateDirectory(excluded);
    var excludedChild = Path.Combine(excluded, "child"); Directory.CreateDirectory(excludedChild);

    var oldCertSub1 = Path.Combine(sub1, "old.pfx");
    File.WriteAllText(oldCertSub1, "old-cert-data");
    File.WriteAllText(Path.Combine(sub2NoCert, "readme.txt"), "not a cert");
    var oldCertExcluded = Path.Combine(excluded, "old.pfx");
    File.WriteAllText(oldCertExcluded, "old-cert-data");
    var oldCertExcludedChild = Path.Combine(excludedChild, "old.pfx");
    File.WriteAllText(oldCertExcludedChild, "old-cert-data");

    var newCertSourceDir = Path.Combine(tmp, "_source"); Directory.CreateDirectory(newCertSourceDir);
    var newCertPath = Path.Combine(newCertSourceDir, "new.pfx");
    File.WriteAllText(newCertPath, "new-cert-data");

    var logs = new List<(LogKind kind, string msg)>();
    var result = CertificateReplacer.Run(new ReplaceOptions
    {
        RootDirectory = tmp,
        NewCertificatePath = newCertPath,
        ExcludeFolders = new[] { "excluded" },
        DryRun = false
    }, (kind, msg) => logs.Add((kind, msg)));

    Check(result.Processed == 1, $"processed count is 1 (was {result.Processed})");
    // sub2-nocert (no certs) and _source (its only file is the source cert itself, excluded) both count as skipped
    Check(result.Skipped == 2, $"skipped count is 2 (was {result.Skipped})");
    Check(!File.Exists(oldCertSub1), "old cert removed from sub1");
    Check(File.Exists(Path.Combine(sub1, "new.pfx")), "new cert installed in sub1 with original name");
    Check(File.ReadAllText(Path.Combine(sub1, "new.pfx")) == "new-cert-data", "installed cert content matches source");
    Check(File.Exists(oldCertExcluded), "excluded folder's cert untouched");
    Check(File.Exists(oldCertExcludedChild), "excluded folder's child cert untouched");
    Check(!File.Exists(Path.Combine(sub2NoCert, "new.pfx")), "folder with no existing certs is left untouched");

    // Dry run should not modify anything
    File.WriteAllText(oldCertSub1, "old-cert-data-2");
    var dryLogs = new List<(LogKind kind, string msg)>();
    CertificateReplacer.Run(new ReplaceOptions
    {
        RootDirectory = tmp,
        NewCertificatePath = newCertPath,
        ExcludeFolders = new[] { "excluded" },
        DryRun = true
    }, (kind, msg) => dryLogs.Add((kind, msg)));
    Check(File.Exists(oldCertSub1), "dry run does not delete old cert");
    Check(File.ReadAllText(oldCertSub1) == "old-cert-data-2", "dry run does not modify old cert content");

    // Read-only handling
    File.WriteAllText(oldCertSub1, "readonly-old");
    File.SetAttributes(oldCertSub1, File.GetAttributes(oldCertSub1) | FileAttributes.ReadOnly);
    if (File.Exists(Path.Combine(sub1, "new.pfx"))) File.Delete(Path.Combine(sub1, "new.pfx"));
    CertificateReplacer.Run(new ReplaceOptions
    {
        RootDirectory = tmp,
        NewCertificatePath = newCertPath,
        ExcludeFolders = new[] { "excluded" },
        DryRun = false
    }, (_, _) => { });
    Check(File.Exists(Path.Combine(sub1, "new.pfx")) && File.ReadAllText(Path.Combine(sub1, "new.pfx")) == "new-cert-data",
        "read-only old cert is removed and replaced");
}
finally
{
    try
    {
        foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(tmp, true);
    }
    catch { /* best-effort cleanup */ }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL TESTS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} TEST(S) FAILED:");
foreach (var f in failures) Console.WriteLine(" - " + f);
return 1;
