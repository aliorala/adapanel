using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Migration.Sources;

internal static class PleskBackupXml
{
    internal static async Task<XElement> ReadAsync(string path, int maxBytes, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > maxBytes) throw Error("xml_size_limit");
        await using var input = File.OpenRead(path);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = maxBytes, MaxCharactersFromEntities = 0, IgnoreComments = true
        });
        var doc = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, token);
        var root = doc.Root ?? throw Error("xml_invalid");
        if (root.Name.LocalName != "migration-dump" || !Attribute(root, "agent-name").StartsWith("Plesk.", StringComparison.Ordinal) || Attribute(root, "dump-format") != "panel")
            throw Error("provider_or_schema_unsupported");
        if (Attribute(root, "content-included") != "true") throw Error("configuration_only");
        if (Attribute(root, "encryption-type") is not ("" or "none")) throw Error("encrypted_backup_unsupported");
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Ancestors().Count() > 64) throw Error("xml_depth_limit");
            if (element.Name.Namespace != root.Name.Namespace) throw Error("xml_namespace_mismatch");
            if (element.Name.LocalName is "incremental" or "incremental-base" or "base-dump" or "volume" or "volume-list" or "split")
                if (element.Value.Trim() is not ("false" or "0")) throw Error("incremental_or_multivolume_unsupported");
            foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                if (attr.Name.LocalName is "incremental" or "multivolume" or "split" or "base-dump" or "base-backup")
                    if (attr.Value is not ("" or "false" or "0")) throw Error("incremental_or_multivolume_unsupported");
        }
        return root;
    }
    internal static IEnumerable<XElement> Children(XElement? element, string name) => element?.Elements().Where(e => e.Name.LocalName == name && e.Name.Namespace == element.Name.Namespace) ?? [];
    internal static XElement? Child(XElement? element, string name) => Children(element, name).SingleOrDefault();
    internal static string Attribute(XElement? element, string name) => element?.Attribute(name)?.Value.Trim() ?? "";
    internal static string Value(XElement? element, string name) => Attribute(element, name) is { Length: > 0 } attr ? attr : Child(element, name)?.Value.Trim() ?? "";
    // Presence matters for TXT: an explicit empty attribute must not fall back to a child,
    // and whitespace-only element content is a valid DNS string.
    internal static string? RawValue(XElement? element, string name)
        => element?.Attribute(name) is { } attribute ? attribute.Value : Child(element, name)?.Value;
    internal static long Number(XElement? element, string name, long fallback = 0) => long.TryParse(Value(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : fallback;
    internal static InvalidDataException Error(string code) => new("migration.plesk." + code);
}
