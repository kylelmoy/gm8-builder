using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Gm8Builder.Tree;

/// <summary>
/// Strict element access for the split tree's XML, in the spirit of gmksplit's
/// own reader: a missing element is an error naming the file, not a default.
/// </summary>
public sealed class Xml(XElement element, string file)
{
    public XElement Element { get; } = element;
    public string File { get; } = file;

    public static Xml Load(string path)
    {
        try
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return new Xml(doc.Root ?? throw new InvalidDataException($"{path}: empty document"), path);
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public string Name => Element.Name.LocalName;

    public Xml Expect(string name) =>
        Name == name ? this : throw new InvalidDataException($"{File}: expected <{name}>, found <{Name}>");

    public bool Has(string name) => Element.Element(name) != null;

    public Xml Child(string name) =>
        new(Element.Element(name) ?? throw new InvalidDataException($"{File}: <{Name}> has no <{name}>"), File);

    public IEnumerable<Xml> Children(string? name = null) =>
        (name == null ? Element.Elements() : Element.Elements(name)).Select(e => new Xml(e, File));

    /// <summary>The element's text, exactly as parsed (so line endings are LF, per XML).</summary>
    public string Text => Element.Value;

    public string Str(string name) => Child(name).Text;
    public int Int(string name) => ParseInt(Str(name), name);
    public double Double(string name) => double.Parse(Str(name).Trim(), CultureInfo.InvariantCulture);
    public bool Bool(string name) => ParseBool(Str(name), name);

    public bool HasAttr(string name) => Element.Attribute(name) != null;

    public string Attr(string name) =>
        Element.Attribute(name)?.Value ?? throw new InvalidDataException($"{File}: <{Name}> has no {name}=\"\"");

    public int AttrInt(string name) => ParseInt(Attr(name), name);
    public bool AttrBool(string name) => ParseBool(Attr(name), name);

    private int ParseInt(string s, string what) =>
        int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidDataException($"{File}: {what} is not an integer: \"{s}\"");

    private bool ParseBool(string s, string what) => s.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidDataException($"{File}: {what} is not a boolean: \"{s}\""),
    };
}
