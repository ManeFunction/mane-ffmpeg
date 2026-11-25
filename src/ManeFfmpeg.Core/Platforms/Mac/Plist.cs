using System.Xml;
using System.Xml.Linq;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>Property-list dictionaries in XML: &lt;key&gt; elements, each followed by its value.</summary>
internal static class Plist
{
    /// <summary>A plist file's top dictionary, or null when it can't be read.</summary>
    public static XElement? Load(string path)
    {
        try
        {
            return File.Exists(path) ? XDocument.Load(path).Root?.Element("dict") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>The value under <paramref name="key"/>, or null.</summary>
    public static XElement? Get(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    /// <summary>The &lt;integer&gt; under <paramref name="key"/>, or null.</summary>
    public static int? Integer(XElement dict, string key) =>
        Get(dict, key) is { Name.LocalName: "integer" } value && int.TryParse(value.Value, out var n) ? n : null;

    /// <summary>The dictionary under <paramref name="key"/>, added when missing.</summary>
    public static XElement Dict(XElement dict, string key)
    {
        if (Get(dict, key) is { Name.LocalName: "dict" } found)
            return found;
        var created = new XElement("dict");
        Set(dict, key, created);
        return created;
    }

    public static IEnumerable<XElement> Values(XElement dict) =>
        dict.Elements("key").Select(k => k.ElementsAfterSelf().FirstOrDefault()).OfType<XElement>();

    public static void Set(XElement dict, string key, XElement value)
    {
        if (dict.Elements("key").FirstOrDefault(k => k.Value == key) is { } existing)
            existing.ElementsAfterSelf().FirstOrDefault()?.ReplaceWith(value);
        else
            dict.Add(new XElement("key", key), value);
    }

    public static void Delete(XElement dict, string key)
    {
        if (dict.Elements("key").FirstOrDefault(k => k.Value == key) is not { } existing)
            return;
        existing.ElementsAfterSelf().FirstOrDefault()?.Remove();
        existing.Remove();
    }
}
