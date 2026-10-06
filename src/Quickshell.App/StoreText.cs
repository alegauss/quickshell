using System.Text;
using System.Text.Json;

namespace Quickshell.App;

/// <summary>
/// The session store's text, written so that what a person typed into it survives the client
/// writing it again (QS117).
///
/// <para><b>Comments are carried by where they were, not by what line they were on.</b> Each one in
/// the file being replaced is anchored to the place in the tree that follows it — a property, a
/// node, the end of an object — and written back before that same place in the new text. A node in
/// a list is anchored by its <c>Name</c> rather than its position, so a comment stays with its
/// session when the sessions around it are added, removed or reordered. A comment on the same line
/// as what precedes it stays at the end of that line.</para>
///
/// <para><b>What cannot be kept is lost honestly.</b> A comment whose anchor is gone — the node was
/// deleted, or renamed, which is the same thing to an anchor by name — goes with it. Whitespace is
/// the writer's, as it always was; only comments are a person's.</para>
///
/// <para><b>With no comments the text is what <see cref="JsonSerializer"/> wrote before this
/// existed</b>, byte for byte, so a store nobody annotated does not change shape under anybody's
/// diff.</para>
/// </summary>
internal static class StoreText
{
    private const string Indent = "  ";

    /// <summary>The tree as indented JSON, with the comments of <paramref name="previous"/> back in place.</summary>
    /// <param name="tree">The serialised tree, as the store's options write it.</param>
    /// <param name="previous">The file being replaced, or empty where there is none.</param>
    internal static byte[] Write(JsonElement tree, ReadOnlySpan<byte> previous)
    {
        Comments comments = Harvest(previous);
        StringBuilder text = new();

        Leading(text, comments, "#top", 0);
        Value(text, tree, string.Empty, 0, comments);
        Trailing(text, comments, string.Empty);
        Leading(text, comments, "#bottom", 0, before: true);

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    // ---- writing ----

    private static void Value(StringBuilder text, JsonElement value, string path, int depth, Comments comments)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Object(text, value, path, depth, comments);
                break;

            case JsonValueKind.Array:
                Array(text, value, path, depth, comments);
                break;

            default:
                text.Append(value.GetRawText());
                break;
        }
    }

    private static void Object(StringBuilder text, JsonElement value, string path, int depth, Comments comments)
    {
        List<JsonProperty> properties = [.. value.EnumerateObject()];
        string end = path + "#end";

        if (properties.Count == 0 && !comments.Has(end))
        {
            text.Append("{}");

            return;
        }

        text.Append('{');

        for (int index = 0; index < properties.Count; index++)
        {
            JsonProperty property = properties[index];
            string at = $"{path}.{property.Name}";

            Line(text, depth + 1);
            Leading(text, comments, at, depth + 1);

            text.Append('"').Append(JsonEncodedText.Encode(property.Name).ToString()).Append("\": ");
            Value(text, property.Value, at, depth + 1, comments);

            if (index < properties.Count - 1)
            {
                text.Append(',');
            }

            Trailing(text, comments, at);
        }

        Closing(text, comments, end, depth);
        text.Append('}');
    }

    private static void Array(StringBuilder text, JsonElement value, string path, int depth, Comments comments)
    {
        List<JsonElement> items = [.. value.EnumerateArray()];
        string end = path + "#end";

        if (items.Count == 0 && !comments.Has(end))
        {
            text.Append("[]");

            return;
        }

        text.Append('[');

        for (int index = 0; index < items.Count; index++)
        {
            string at = Element(path, items[index], index);

            Line(text, depth + 1);
            Leading(text, comments, at, depth + 1);
            Value(text, items[index], at, depth + 1, comments);

            if (index < items.Count - 1)
            {
                text.Append(',');
            }

            Trailing(text, comments, at);
        }

        Closing(text, comments, end, depth);
        text.Append(']');
    }

    /// <summary>The comments that stood before the end of a container, then the line its bracket is on.</summary>
    private static void Closing(StringBuilder text, Comments comments, string end, int depth)
    {
        if (comments.Take(end, trailing: false) is { Count: > 0 } before)
        {
            foreach (string comment in before)
            {
                Line(text, depth + 1);
                text.Append(comment);
            }
        }

        Line(text, depth);
    }

    /// <summary>Comments on lines of their own before an anchor, each followed by the anchor's indent.</summary>
    private static void Leading(StringBuilder text, Comments comments, string at, int depth, bool before = false)
    {
        foreach (string comment in comments.Take(at, trailing: false))
        {
            if (before)
            {
                Line(text, depth);
            }

            text.Append(comment);

            if (!before)
            {
                Line(text, depth);
            }
        }
    }

    /// <summary>Comments that were on the same line as the anchor before them.</summary>
    private static void Trailing(StringBuilder text, Comments comments, string at)
    {
        foreach (string comment in comments.Take(at, trailing: true))
        {
            text.Append(' ').Append(comment);
        }
    }

    private static void Line(StringBuilder text, int depth)
    {
        text.Append(Environment.NewLine);

        for (int level = 0; level < depth; level++)
        {
            text.Append(Indent);
        }
    }

    /// <summary>
    /// An element's anchor: its <c>Name</c> where it has one, so it is found again wherever it moved
    /// to, and its position otherwise.
    /// </summary>
    private static string Element(string path, JsonElement item, int index) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(nameof(SessionNode.Name), out JsonElement name)
        && name.ValueKind == JsonValueKind.String
            ? $"{path}[{name.GetString()}]"
            : $"{path}[#{index}]";

    // ---- reading ----

    /// <summary>
    /// Every comment in the old text, anchored. Anything that will not parse yields none: the store
    /// is about to be written whole, and a broken old file has nothing reliable to anchor to.
    /// </summary>
    private static Comments Harvest(ReadOnlySpan<byte> previous)
    {
        Comments found = new();

        // A file saved by an editor that writes a byte-order mark is still the user's file.
        if (previous.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            previous = previous[3..];
        }

        if (previous.IsEmpty)
        {
            return found;
        }

        try
        {
            Read(previous, found);
        }
        catch (JsonException)
        {
            return new Comments();
        }

        return found;
    }

    private static void Read(ReadOnlySpan<byte> previous, Comments found)
    {
        Utf8JsonReader reader = new(previous, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });

        // Each open container: its path, whether it is an array, how many elements it has had, and
        // the property name waiting for its value.
        Stack<Frame> open = new();
        List<string> pending = [];
        string? last = null;
        long lastEnd = 0;
        bool beforeRoot = true;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Comment)
            {
                int start = (int)reader.TokenStartIndex;
                string raw = Encoding.UTF8.GetString(previous[start..(int)reader.BytesConsumed]).TrimEnd();
                bool sameLine = last is not null && !previous[(int)lastEnd..start].Contains((byte)'\n');

                if (sameLine)
                {
                    found.Add(last!, raw, trailing: true);
                }
                else
                {
                    pending.Add(raw);
                }

                continue;
            }

            string anchor = Anchor(ref reader, open, beforeRoot);

            foreach (string text in pending)
            {
                found.Add(anchor, text, trailing: false);
            }

            pending.Clear();
            beforeRoot = false;

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    open.Peek().Property = reader.GetString();
                    last = anchor;
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    open.Push(new Frame(Path(open, anchor), reader.TokenType == JsonTokenType.StartArray));
                    last = null;
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    Frame closed = open.Pop();
                    last = closed.Path.Length == 0 ? string.Empty : closed.Path;
                    Advance(open);
                    break;

                default:
                    last = open.Count > 0 && open.Peek().IsArray ? anchor : open.Count > 0 ? Current(open) : string.Empty;
                    Advance(open);
                    break;
            }

            lastEnd = reader.BytesConsumed;
        }

        foreach (string text in pending)
        {
            found.Add("#bottom", text, trailing: false);
        }
    }

    /// <summary>Where the token the reader is on sits in the tree.</summary>
    private static string Anchor(ref Utf8JsonReader reader, Stack<Frame> open, bool beforeRoot)
    {
        if (beforeRoot)
        {
            return "#top";
        }

        Frame frame = open.Peek();

        if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
        {
            return frame.Path + "#end";
        }

        if (!frame.IsArray)
        {
            return reader.TokenType == JsonTokenType.PropertyName
                ? $"{frame.Path}.{reader.GetString()}"
                : Current(open);
        }

        // An element: by its Name when it is an object that has one, which needs reading ahead.
        if (reader.TokenType == JsonTokenType.StartObject && NameAhead(reader) is { } name)
        {
            return $"{frame.Path}[{name}]";
        }

        return $"{frame.Path}[#{frame.Elements}]";
    }

    /// <summary>The <c>Name</c> of the object starting here, read from a copy of the reader.</summary>
    private static string? NameAhead(Utf8JsonReader ahead)
    {
        int depth = ahead.CurrentDepth;

        while (ahead.Read())
        {
            // Without regard to case, as the store is read: a hand-typed "name" is the Name.
            if (ahead.TokenType == JsonTokenType.PropertyName && ahead.CurrentDepth == depth + 1
                && string.Equals(ahead.GetString(), nameof(SessionNode.Name), StringComparison.OrdinalIgnoreCase))
            {
                ahead.Read();

                return ahead.TokenType == JsonTokenType.String ? ahead.GetString() : null;
            }

            if (ahead.TokenType == JsonTokenType.EndObject && ahead.CurrentDepth == depth)
            {
                return null;
            }

            if (ahead.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray
                && ahead.CurrentDepth > depth)
            {
                ahead.Skip();
            }
        }

        return null;
    }

    /// <summary>The path a container opened at this anchor has.</summary>
    private static string Path(Stack<Frame> open, string anchor) =>
        open.Count == 0 ? string.Empty : anchor;

    /// <summary>The path of the property whose value is being read.</summary>
    private static string Current(Stack<Frame> open) => $"{open.Peek().Path}.{open.Peek().Property}";

    private static void Advance(Stack<Frame> open)
    {
        if (open.Count > 0 && open.Peek().IsArray)
        {
            open.Peek().Elements++;
        }
    }

    private sealed class Frame(string path, bool isArray)
    {
        public string Path { get; } = path;

        public bool IsArray { get; } = isArray;

        public int Elements { get; set; }

        public string? Property { get; set; }
    }

    /// <summary>
    /// Comments by anchor, each taken once so none is written twice. Anchors match without regard to
    /// case, as reading does: a person who typed <c>"settings"</c> wrote the property the writer
    /// spells <c>"Settings"</c>.
    /// </summary>
    private sealed class Comments
    {
        private readonly Dictionary<string, List<string>> _by = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string anchor, string text, bool trailing)
        {
            string key = Key(anchor, trailing);

            if (!_by.TryGetValue(key, out List<string>? list))
            {
                _by[key] = list = [];
            }

            list.Add(text);
        }

        public bool Has(string anchor) => _by.ContainsKey(Key(anchor, trailing: false));

        public List<string> Take(string anchor, bool trailing) =>
            _by.Remove(Key(anchor, trailing), out List<string>? list) ? list : [];

        private static string Key(string anchor, bool trailing) => trailing ? anchor + "\0after" : anchor;
    }
}
