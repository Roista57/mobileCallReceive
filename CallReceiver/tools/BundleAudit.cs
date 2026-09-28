// MSBuild-only task; this file is not compiled into the application.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class BundleAudit : Task
{
    public ITaskItem[] Candidates { get; set; } = new ITaskItem[0];
    public ITaskItem[] External { get; set; } = new ITaskItem[0];
    [Required] public string Report { get; set; }
    public string PublishDirectory { get; set; }
    public string Executable { get; set; }

    private static string Kind(string path, out string culture)
    {
        culture = "";
        try
        {
            var assembly = AssemblyName.GetAssemblyName(path);
            culture = assembly.CultureName ?? "";
            return culture.Length == 0 ? "managed" : "resource";
        }
        catch (BadImageFormatException) { return "native"; }
    }

    public override bool Execute()
    {
        try
        {
            if (string.IsNullOrEmpty(PublishDirectory)) InspectInputs();
            else InspectOutput();
        }
        catch (Exception error) { Log.LogErrorFromException(error); }
        return !Log.HasLoggedErrors;
    }

    private void InspectInputs()
    {
        var lines = new List<string>();
        int korean = 0, managed = 0, native = 0;
        foreach (var item in External)
        {
            if (!item.ItemSpec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string culture;
            if (Kind(item.ItemSpec, out culture) != "native")
                Log.LogError("Managed/resource DLL excluded from bundle: " + item.ItemSpec);
        }
        foreach (var item in Candidates)
        {
            if (!item.ItemSpec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string culture;
            var kind = Kind(item.ItemSpec, out culture);
            var relative = item.GetMetadata("RelativePath");
            if (string.IsNullOrEmpty(relative)) throw new InvalidDataException("Missing SDK RelativePath: " + item.ItemSpec);
            if (kind == "resource")
            {
                if (culture != "ko" && culture != "en") Log.LogError("Unexpected resource culture: " + culture);
                if (culture == "ko") korean++;
            }
            else if (kind == "managed") managed++;
            else native++;
            // SDK paths + PE assembly metadata, not a hand-maintained DLL name list.
            lines.Add(kind + "\t" + relative + "\t" + item.GetMetadata("AssetType") + "\t" + culture);
        }
        // Native assets explicitly marked external by the SDK also remain required.
        foreach (var item in External)
        {
            if (!item.ItemSpec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string culture;
            if (Kind(item.ItemSpec, out culture) == "native")
            {
                lines.Add("native\t" + item.GetMetadata("RelativePath") + "\t" + item.GetMetadata("AssetType") + "\t");
                native++;
            }
        }
        if (korean == 0 || managed == 0 || native == 0) Log.LogError("Incomplete bundle input: managed, native and Korean resources are required.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Report)));
        File.WriteAllLines(Report, lines);
        Log.LogMessage(MessageImportance.High, "Bundle audit: {0} managed, {1} Korean resources, {2} external native DLLs. Report: {3}", managed, korean, native, Report);
    }

    private void InspectOutput()
    {
        var root = Path.GetFullPath(PublishDirectory);
        if (!File.Exists(Path.Combine(root, Executable))) Log.LogError("Published executable is missing.");
        var lines = File.ReadAllLines(Report);
        if (lines.Length == 0) throw new InvalidDataException("Empty bundle audit report.");
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            var path = Path.GetFullPath(Path.Combine(root, fields[1]));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Publish path outside output directory.");
            if (fields[0] == "native" && !File.Exists(path)) Log.LogError("Missing external native DLL: " + fields[1]);
            if (fields[0] != "native" && File.Exists(path)) Log.LogError("Bundled DLL remains external: " + fields[1]);
        }
        foreach (var file in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            string culture;
            if (Kind(file, out culture) != "native") Log.LogError("Unexpected external managed/resource DLL: " + file);
        }
        Log.LogMessage(MessageImportance.High, "Bundle output audit complete: " + root);
    }
}
