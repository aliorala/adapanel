using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AdaPanel.Infrastructure.Migration.Sources;

internal static class CpanelYaml
{
    internal static YamlMappingNode Read(string text)
    {
        var parser = new Parser(new StringReader(text));
        var count = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            if (++count > 100_000) throw new InvalidDataException("migration.cpanel.yaml_item_limit");
            if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw new InvalidDataException("migration.cpanel.yaml_alias_or_tag");
            if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw new InvalidDataException("migration.cpanel.yaml_depth");
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping) throw new InvalidDataException("migration.cpanel.yaml_mapping_required");
        return mapping;
    }
    internal static string Scalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar ? scalar.Value ?? "" : "";
    internal static List<string> Strings(YamlMappingNode mapping, string key)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)) return [];
        return node switch
        {
            YamlScalarNode scalar => (scalar.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList(),
            YamlSequenceNode sequence when sequence.Children.All(n => n is YamlScalarNode) => sequence.Children.Cast<YamlScalarNode>().Select(n => n.Value ?? "").ToList(),
            _ => throw new InvalidDataException("migration.cpanel.yaml_field_invalid")
        };
    }
}
