using System.Diagnostics;
using System.Xml.Linq;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>What the system remembers about a Quick Action's menu item, by the item's name.</summary>
public interface IServicesMenu
{
    /// <summary>
    /// A workflow named <paramref name="name"/> was added (or renamed from <paramref name="previousName"/>): enable it,
    /// and put it at the end of Finder's Quick Actions, or where the previous name was.
    /// </summary>
    void Added(string name, string? previousName);

    /// <summary>The workflow named <paramref name="name"/> is gone: forget its settings.</summary>
    void Removed(string name);
}

/// <summary>
/// Finder's settings for services, in the "pbs" preferences (what System Settings → Extensions → Finder edits). Each
/// is keyed by the menu item: NSServicesStatus by "(null) - name - runWorkflowAsService", FinderOrdering and FinderActive
/// by "SERVICE-" and the same. A workflow without an NSServicesStatus entry with FinderPreview 1 is disabled, and one
/// without a FinderOrdering number is listed after the numbered ones, alphabetically. The preferences are read with
/// <c>defaults export</c> and written back whole with <c>defaults import</c>, which goes through cfprefsd (editing the
/// file directly would be undone by its cache).
/// </summary>
public sealed class FinderServices : IServicesMenu
{
    private const string Domain = "pbs";

    public void Added(string name, string? previousName) => Update(prefs => Add(prefs, name, previousName));

    public void Removed(string name) => Update(prefs => Remove(prefs, name));

    internal static string StatusKey(string name) => $"(null) - {name} - runWorkflowAsService";

    internal static string FinderKey(string name) => "SERVICE-" + StatusKey(name);

    /// <summary>
    /// Enables <paramref name="name"/> and numbers it after everything in Finder's ordering, or gives it the number
    /// <paramref name="previousName"/> had, whose entries are removed.
    /// </summary>
    internal static void Add(XElement prefs, string name, string? previousName)
    {
        var ordering = Dict(prefs, "FinderOrdering");
        int? position = null;
        if (previousName is not null && previousName != name)
        {
            position = Get(ordering, FinderKey(previousName)) is { Name.LocalName: "integer" } old && int.TryParse(old.Value, out var n) ? n : null;
            Remove(prefs, previousName);
        }
        position ??= Get(ordering, FinderKey(name)) is { Name.LocalName: "integer" } own && int.TryParse(own.Value, out var kept)
            ? kept
            : Values(ordering).Select(v => int.TryParse(v.Value, out var i) ? i : -1).DefaultIfEmpty(-1).Max() + 1;
        Set(ordering, FinderKey(name), new XElement("integer", position));
        Set(Dict(prefs, "FinderActive"), FinderKey(name), new XElement("integer", 1));
        Set(Dict(prefs, "NSServicesStatus"), StatusKey(name), new XElement("dict",
            new XElement("key", "presentation_modes"),
            new XElement("dict",
                new XElement("key", "ContextMenu"), new XElement("true"),
                new XElement("key", "FinderPreview"), new XElement("true"),
                new XElement("key", "ServicesMenu"), new XElement("true"),
                new XElement("key", "TouchBar"), new XElement("true"))));
    }

    internal static void Remove(XElement prefs, string name)
    {
        Delete(Dict(prefs, "FinderOrdering"), FinderKey(name));
        Delete(Dict(prefs, "FinderActive"), FinderKey(name));
        Delete(Dict(prefs, "NSServicesStatus"), StatusKey(name));
    }

    private static void Update(Action<XElement> change)
    {
        try
        {
            var export = Run(["export", Domain, "-"], null);
            var document = XDocument.Parse(export);
            var prefs = document.Root?.Element("dict") ?? throw new InvalidDataException("No dictionary in the pbs preferences.");
            change(prefs);
            Run(["import", Domain, "-"], Serialize(document));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Couldn't update Finder's Quick Actions settings: {ex.Message}");
        }
    }

    /// <summary>
    /// The plist as text for <c>defaults import</c>. XDocument writes the plist DOCTYPE with an empty internal subset
    /// ("…dtd"[]>), which the property-list parser rejects, so it's left out; the parser doesn't need it.
    /// </summary>
    internal static string Serialize(XDocument document) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + document.Root!.ToString();

    private static string Run(IReadOnlyList<string> args, string? input)
    {
        var start = new ProcessStartInfo("/usr/bin/defaults")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("defaults didn't start.");
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"defaults {string.Join(" ", args)}: {error.Trim()}");
        return output.Result;
    }

    // Property-list dictionaries: <key> elements, each followed by its value.

    /// <summary>The dictionary under <paramref name="key"/>, added when missing.</summary>
    internal static XElement Dict(XElement dict, string key)
    {
        if (Get(dict, key) is { Name.LocalName: "dict" } found)
            return found;
        var created = new XElement("dict");
        Set(dict, key, created);
        return created;
    }

    private static XElement? Get(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static IEnumerable<XElement> Values(XElement dict) =>
        dict.Elements("key").Select(k => k.ElementsAfterSelf().FirstOrDefault()).OfType<XElement>();

    private static void Set(XElement dict, string key, XElement value)
    {
        if (dict.Elements("key").FirstOrDefault(k => k.Value == key) is { } existing)
            existing.ElementsAfterSelf().FirstOrDefault()?.ReplaceWith(value);
        else
            dict.Add(new XElement("key", key), value);
    }

    private static void Delete(XElement dict, string key)
    {
        if (dict.Elements("key").FirstOrDefault(k => k.Value == key) is not { } existing)
            return;
        existing.ElementsAfterSelf().FirstOrDefault()?.Remove();
        existing.Remove();
    }
}
