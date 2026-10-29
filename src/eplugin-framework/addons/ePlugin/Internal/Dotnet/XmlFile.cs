#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// An XML file the project commits and shares (<c>nuget.config</c>, project files), read with what <see cref="Write"/>
/// needs to write it back unchanged apart from the edited nodes: its line breaks, byte order mark and declaration.
/// </summary>
internal sealed record XmlFile(XDocument Document, string NewLine, bool ByteOrderMark)
{
    public static XmlFile Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // the reader turns every line break into \n; Write writes them back as the file had them.
        var newLine = Array.IndexOf(bytes, (byte)'\r') >= 0 ? "\r\n" : "\n";
        using var stream = new MemoryStream(bytes);
        return new(XDocument.Load(stream, LoadOptions.PreserveWhitespace), newLine, bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    /// <summary>Writes the file back in one step, keeping its formatting and declaration.</summary>
    public void Write(string path)
    {
        AtomicFile.Write(path, stream =>
        {
            using var text = new StreamWriter(stream, new UTF8Encoding(ByteOrderMark), leaveOpen: true);
            if (Document.Declaration is { } declaration)
            {
                // the writer would add or change the encoding; it writes UTF-8.
                declaration.Encoding = declaration.Encoding is null ? null : "utf-8";
                text.Write(declaration.ToString());
            }

            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, NewLineChars = NewLine, NewLineHandling = NewLineHandling.Replace };
            using var writer = XmlWriter.Create(text, settings);
            Document.Save(writer);
        });
    }

    /// <summary>
    /// Adds <paramref name="nodes"/> after the last child of <paramref name="parent"/>, each on its own line and indented
    /// like its siblings, or on the same line where the file keeps them on one.
    /// </summary>
    public static void AddLines(XElement parent, params XNode[] nodes)
    {
        var sibling = parent.Nodes().FirstOrDefault(n => !IsWhitespace(n));
        var closing = parent.LastNode is XText text && IsWhitespace(text) ? text : null;
        var indent = sibling is not null ? IndentBefore(sibling)
            : closing is not null && closing.Value.Contains('\n') ? LastLine(closing.Value) + "  "
            : IndentBefore(parent) is { } outer ? outer + "  " : null;
        if (indent is null)
        {
            parent.Add(nodes);
            return;
        }

        var lines = nodes.SelectMany(node => new XNode[] { new XText("\n" + indent), node }).ToArray();
        if (closing is not null)
        {
            closing.AddBeforeSelf(lines);
        }
        else
        {
            parent.Add(lines, new XText("\n" + (IndentBefore(parent) ?? "")));
        }
    }

    /// <summary>Removes a node together with the line break and indentation before it.</summary>
    public static void RemoveLine(XNode node)
    {
        if (node.PreviousNode is { } previous && IsWhitespace(previous))
        {
            previous.Remove();
        }

        node.Remove();
    }

    /// <summary>The indentation of a node that starts its own line; null when it follows other content on its line.</summary>
    public static string? IndentBefore(XNode node)
    {
        if (node is XElement { Parent: null })
        {
            return "";
        }

        return node.PreviousNode is XText text && IsWhitespace(text) && text.Value.Contains('\n') ? LastLine(text.Value) : null;
    }

    public static bool IsWhitespace(XNode node) => node is XText text && string.IsNullOrWhiteSpace(text.Value);

    private static string LastLine(string value) => value[(value.LastIndexOf('\n') + 1)..];
}
#endif
