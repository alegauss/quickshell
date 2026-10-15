using System.Globalization;
using System.IO;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// A PDF of chosen sessions as they resolve, each value beside the node that set it (QS315): what a
/// person hands a colleague, files with a change request, or keeps to rebuild a machine, instead of
/// the sessions file read by hand, where an inherited value is not even on the node that uses it.
///
/// <para><b>No secret is ever in it, and nothing here can read one.</b> A password is named by the
/// credential it is saved under, and whether one is saved is a question asked of a delegate that
/// answers yes or no. A key is named by its file and its fingerprint, which <see cref="KeyFingerprint"/>
/// reads from the public half alone. A file a person may mail anywhere has to be safe to mail.</para>
///
/// <para><b>PDFsharp with MigraDoc</b>, the owner's choice on 2026-10-15: MIT and managed, so it
/// costs no licence and no native library. QuestPDF is free only under a revenue limit, and Microsoft
/// Print to PDF needs a dialog and a printer. Loaded only when a document is written: nothing on the
/// way to a first frame touches this type.</para>
/// </summary>
public static class SessionDocument
{
    private const string Font = "Segoe UI";
    private const string Fixed = "Consolas";

    // The document's own paragraph styles, beside MigraDoc's built-in headings.
    private const string Cover = "Cover";
    private const string Quiet = "Quiet";
    private const string Listed = "Listed";
    private const string Typed = "Typed";

    private static readonly Lock FontsChosen = new();

    private static bool _fontsChosen;

    /// <summary>
    /// Writes the document of <paramref name="sessions"/> to <paramref name="file"/>.
    /// </summary>
    /// <param name="sessions">The sessions, in the order they are documented.</param>
    /// <param name="file">The PDF to write; replaced where it is there.</param>
    /// <param name="when">When it was written, for the cover.</param>
    /// <param name="version">The client's version, for the cover.</param>
    /// <param name="saved">Whether a password is saved under a credential name: yes or no, never the password.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Write(IReadOnlyList<ResolvedSession> sessions, string file, DateTimeOffset when, string version,
                             Func<string, bool> saved)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(saved);

        UseWindowsFonts();

        PdfDocumentRenderer renderer = new() { Document = Build(sessions, when, version, saved) };

        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = $"quickshell {version}";
        renderer.PdfDocument.Save(file);
    }

    /// <summary>The document itself, before it is laid out.</summary>
    private static Document Build(IReadOnlyList<ResolvedSession> sessions, DateTimeOffset when, string version,
                                  Func<string, bool> saved)
    {
        Document document = new();

        document.Info.Title = "Sessions";
        document.Info.Subject = $"{sessions.Count} sessions, as quickshell resolves them";

        Styles(document);

        Section cover = document.AddSection();

        cover.PageSetup = document.DefaultPageSetup.Clone();
        cover.PageSetup.PageFormat = PageFormat.A4;
        cover.PageSetup.TopMargin = cover.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        cover.PageSetup.LeftMargin = cover.PageSetup.RightMargin = Unit.FromCentimeter(2);

        Paragraph footer = cover.Footers.Primary.AddParagraph();

        footer.Style = Quiet;
        footer.AddText("Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        cover.AddParagraph("Sessions", Cover);
        cover.AddParagraph(string.Create(CultureInfo.InvariantCulture,
            $"Written {when.ToLocalTime():yyyy-MM-dd HH:mm} by quickshell {version}."));

        string[] folders = [.. sessions.Select(session => Folder(session.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                       .Order(StringComparer.OrdinalIgnoreCase)];

        cover.AddParagraph($"{Count(sessions.Count, "session")}, from {Count(folders.Length, "folder")}:");

        foreach (string folder in folders)
        {
            cover.AddParagraph(folder.Length == 0 ? "the top level" : folder, Listed);
        }

        cover.AddParagraph(
            "Each value says where it comes from: the session itself, or the folder it inherits it from. "
            + "No password, passphrase or private key is in this document. A saved password is named by the "
            + "credential it is saved under, and a key by its file and its fingerprint.", Quiet);

        foreach (ResolvedSession session in sessions)
        {
            cover.AddPageBreak();
            Describe(cover, session, saved);
        }

        return document;
    }

    private static void Describe(Section section, ResolvedSession session, Func<string, bool> saved)
    {
        Paragraph heading = section.AddParagraph(Leaf(session.Path), StyleNames.Heading1);

        heading.AddBookmark(session.Path);

        string folder = Folder(session.Path);

        section.AddParagraph(folder.Length == 0 ? "At the top level." : $"In {folder}.", Quiet);

        Table settings = Settings(section);

        Add(settings, "Host", session.Host, "this session");
        Add(settings, "Port", session.Port, session, "22, the default");
        Add(settings, "User", session.User, session, "not set: the Windows account name is used");
        Add(settings, "Signs in with", SignsIn(session, saved), SignInFrom(session));

        if (session.Key is { } key)
        {
            Add(settings, "Key file", key.Value, From(key.From, session));

            (string? fingerprint, string why) = KeyFingerprint.Of(RemoteShell.Expand(key.Value));

            Add(settings, "Key fingerprint", fingerprint ?? $"not read: {why}", From(key.From, session));
        }

        if (session.Credential is { } credential)
        {
            Add(settings, "Saved credential", credential.Value, From(credential.From, session));
        }

        Add(settings, "Reconnect after a drop", session.Reconnect is { } again ? (again.Value ? "yes" : "no") : "no, the default",
            session.Reconnect is { } said ? From(said.From, session) : string.Empty);

        Optional(settings, "Terminal type", session.TerminalType, session);
        Optional(settings, "Colour scheme", session.Scheme, session);
        Optional(settings, "Font size", session.FontSize, session);
        Optional(settings, "Scrollback", session.Scrollback, session);

        if (session.Tags.Count > 0)
        {
            Add(settings, "Tags", string.Join(", ", session.Tags), "this session and its folders");
        }

        Chain(section, session);
        Forwards(section, session);

        if (session.PostLogin is { Length: > 0 } typed)
        {
            section.AddParagraph("Typed after login", StyleNames.Heading2);

            foreach (string line in typed.ReplaceLineEndings("\n").Split('\n'))
            {
                section.AddParagraph(line, Typed);
            }
        }
    }

    /// <summary>How the session signs in, read as a connection reads it: a key file, a saved password, then the agent.</summary>
    private static string SignsIn(ResolvedSession session, Func<string, bool> saved) =>
        session.Key is not null ? "the key file below"
        : session.Credential?.Value is { Length: > 0 } named
            ? saved(named)
                ? $"a password, saved under the credential \"{named}\""
                : $"a password, asked for each time: none is saved under \"{named}\" on this machine"
        : "the agent and the keys OpenSSH looks for in ~/.ssh, then a password where those are refused";

    private static string SignInFrom(ResolvedSession session) =>
        session.Key is { } key ? From(key.From, session)
        : session.Credential is { } credential ? From(credential.From, session)
        : "nothing set, so the default";

    /// <summary>The hops a connection goes through, in order, then the session itself.</summary>
    private static void Chain(Section section, ResolvedSession session)
    {
        if (session.JumpHost is not { } jump)
        {
            return;
        }

        string user = session.User?.Value ?? Environment.UserName;

        section.AddParagraph("Jump chain", StyleNames.Heading2);
        section.AddParagraph($"Set by {From(jump.From, session)}: {jump.Value}", Quiet);

        Table hops = Grid(section, (1.2, "Hop"), (8.3, "Address"), (7.5, "Signs in as"));
        int number = 1;

        foreach (SshEndpoint hop in RemoteShell.Hops(jump.Value, user))
        {
            Row(hops, number++.ToString(CultureInfo.InvariantCulture), Address(hop.Host, hop.Port), hop.User);
        }

        Row(hops, "then", Address(session.Host, session.Port?.Value ?? SshEndpoint.DefaultPort), user);
    }

    private static void Forwards(Section section, ResolvedSession session)
    {
        if (session.Forwards.Count == 0)
        {
            return;
        }

        section.AddParagraph("Forwards", StyleNames.Heading2);
        section.AddParagraph("Opened with the session, and the session's own: a folder never sets one.", Quiet);

        Table forwards = Grid(section, (3, "Kind"), (6, "Listens on"), (8, "Reaches"));

        foreach (ForwardSpec forward in session.Forwards)
        {
            string listens = forward.Kind == ForwardKind.Remote
                ? string.Create(CultureInfo.InvariantCulture, $"port {forward.ListenPort} on the server")
                : Address(forward.Bind ?? "127.0.0.1", forward.ListenPort) + (forward.ListenPort == 0 ? " (any free port)" : string.Empty);

            string reaches = forward.Kind == ForwardKind.Dynamic
                ? "any host, as a SOCKS proxy through the server"
                : Address(forward.TargetHost ?? string.Empty, forward.TargetPort)
                  + (forward.Kind == ForwardKind.Remote ? " from this machine" : " from the server");

            Row(forwards, forward.Kind.ToString(), listens, reaches);
        }
    }

    private static Table Settings(Section section) => Grid(section, (4.2, "Setting"), (7.6, "Value"), (5.2, "Set by"));

    private static Table Grid(Section section, params (double Centimetres, string Heading)[] columns)
    {
        Table table = section.AddTable();

        table.Borders.Color = Colors.LightGray;
        table.Borders.Width = 0.5;
        table.TopPadding = table.BottomPadding = Unit.FromPoint(2);
        table.LeftPadding = table.RightPadding = Unit.FromPoint(4);

        foreach ((double width, string _) in columns)
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }

        Row header = table.AddRow();

        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = Colors.WhiteSmoke;

        for (int at = 0; at < columns.Length; at++)
        {
            header.Cells[at].AddParagraph(columns[at].Heading);
        }

        return table;
    }

    private static void Row(Table table, params string[] cells)
    {
        Row row = table.AddRow();

        for (int at = 0; at < cells.Length; at++)
        {
            row.Cells[at].AddParagraph(cells[at]);
        }
    }

    private static void Add(Table table, string setting, string value, string from) => Row(table, setting, value, from);

    private static void Add<T>(Table table, string setting, Source<T>? value, ResolvedSession session, string otherwise)
    {
        if (value is { } set)
        {
            Row(table, setting, Text(set.Value), From(set.From, session));
        }
        else
        {
            Row(table, setting, otherwise, "nothing set, so the default");
        }
    }

    private static void Optional<T>(Table table, string setting, Source<T>? value, ResolvedSession session)
    {
        if (value is { } set)
        {
            Row(table, setting, Text(set.Value), From(set.From, session));
        }
    }

    /// <summary>The node that set a value, as a reader names it.</summary>
    private static string From(string node, ResolvedSession session) =>
        string.Equals(node, session.Path, StringComparison.OrdinalIgnoreCase) ? "this session"
        : node is "/" or "" ? "inherited from the top level"
        : $"inherited from the folder {node}";

    private static string Text<T>(T value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Address(string host, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{port}");

    private static string Folder(string path) => path.LastIndexOf('/') is var at and > 0 ? path[..at] : string.Empty;

    private static string Leaf(string path) => path.LastIndexOf('/') is var at and >= 0 ? path[(at + 1)..] : path;

    private static string Count(int count, string what) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {what}{(count == 1 ? string.Empty : "s")}");

    private static void Styles(Document document)
    {
        Style normal = document.Styles[StyleNames.Normal]!;

        normal.Font.Name = Font;
        normal.Font.Size = 9.5;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        Style title = document.Styles.AddStyle(Cover, StyleNames.Normal);

        title.Font.Size = 22;
        title.Font.Bold = true;
        title.ParagraphFormat.SpaceAfter = Unit.FromPoint(12);

        Style first = document.Styles[StyleNames.Heading1]!;

        first.Font.Name = Font;
        first.Font.Size = 15;
        first.Font.Bold = true;
        first.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        first.ParagraphFormat.KeepWithNext = true;

        Style second = document.Styles[StyleNames.Heading2]!;

        second.Font.Name = Font;
        second.Font.Size = 11.5;
        second.Font.Bold = true;
        second.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
        second.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        second.ParagraphFormat.KeepWithNext = true;

        Style quiet = document.Styles.AddStyle(Quiet, StyleNames.Normal);

        quiet.Font.Color = Colors.DimGray;
        quiet.Font.Size = 8.5;

        Style listed = document.Styles.AddStyle(Listed, StyleNames.Normal);

        listed.ParagraphFormat.LeftIndent = Unit.FromCentimeter(0.6);
        listed.ParagraphFormat.SpaceAfter = Unit.FromPoint(1);

        Style typed = document.Styles.AddStyle(Typed, StyleNames.Normal);

        typed.Font.Name = Fixed;
        typed.Font.Size = 8.5;
        typed.ParagraphFormat.SpaceAfter = Unit.Zero;
    }

    /// <summary>
    /// PDFsharp's managed build finds no font by itself, and 6.2's own Windows lookup knows only the
    /// core families, so the two this document uses are handed to it from the Windows fonts folder,
    /// once per process, before the first document.
    /// </summary>
    private static void UseWindowsFonts()
    {
        lock (FontsChosen)
        {
            if (!_fontsChosen)
            {
                PdfSharp.Fonts.GlobalFontSettings.FontResolver = new WindowsFonts();
                _fontsChosen = true;
            }
        }
    }

    /// <summary>Segoe UI for the text and Consolas for what is typed, each in its four faces; anything else is Segoe UI.</summary>
    private sealed class WindowsFonts : PdfSharp.Fonts.IFontResolver
    {
        public PdfSharp.Fonts.FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            string stem = string.Equals(familyName, Fixed, StringComparison.OrdinalIgnoreCase) ? "consola" : "segoeui";
            string face = (bold, italic) switch
            {
                (true, true) => "z",
                (true, false) => "b",
                (false, true) => "i",
                _ => string.Empty,
            };

            return new PdfSharp.Fonts.FontResolverInfo(stem + face);
        }

        public byte[]? GetFont(string faceName) =>
            File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), faceName + ".ttf"));
    }
}
