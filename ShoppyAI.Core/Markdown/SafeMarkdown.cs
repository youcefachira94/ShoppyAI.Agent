using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigBlock = Markdig.Syntax.Block;
using MarkdigInline = Markdig.Syntax.Inlines.Inline;

namespace ShoppyAI.Core.Markdown;

public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdCodeBlock(string? Language, string Code) : MdBlock;
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows, bool HasHeader) : MdBlock;
public sealed record MdRule : MdBlock;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdStrikethrough(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdCode(string Code) : MdInline;
public sealed record MdLineBreak : MdInline;

/// <summary>A link. <see cref="Url"/> is null when the target is not an http(s) address and must not be opened.</summary>
public sealed record MdLink(IReadOnlyList<MdInline> Children, Uri? Url, string RawTarget) : MdInline;

/// <summary>
/// Converts model output (untrusted) into a small document model that a UI can render
/// without running anything: raw HTML is shown as text, images are never downloaded,
/// and only http/https links are kept as links.
/// </summary>
public static class SafeMarkdown
{
    private const int MaxDepth = 12;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(markdown, Pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses absurdly nested input: show it as plain text.
            return [new MdParagraph([new MdText(markdown)])];
        }

        return ConvertBlocks(document, 0);
    }

    public static Uri? SafeUrl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            !Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }

    /// <summary>Plain text of inlines, e.g. for accessibility or direction detection.</summary>
    public static string ToPlainText(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdCode c => c.Code,
        MdLineBreak => "\n",
        MdStrong s => ToPlainText(s.Children),
        MdEmphasis e => ToPlainText(e.Children),
        MdStrikethrough s => ToPlainText(s.Children),
        MdLink l => ToPlainText(l.Children),
        _ => ""
    }));

    private static List<MdBlock> ConvertBlocks(ContainerBlock container, int depth)
    {
        var blocks = new List<MdBlock>();
        foreach (var block in container)
        {
            if (ConvertBlock(block, depth) is { } converted)
                blocks.Add(converted);
        }

        return blocks;
    }

    private static MdBlock? ConvertBlock(MarkdigBlock block, int depth)
    {
        if (depth > MaxDepth)
            return new MdParagraph([new MdText(RawText(block))]);

        switch (block)
        {
            case HeadingBlock heading:
                return new MdHeading(heading.Level, ConvertInlines(heading.Inline, depth));

            case FencedCodeBlock fenced:
                return new MdCodeBlock(string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info.Trim(), CodeText(fenced));

            case CodeBlock code:
                return new MdCodeBlock(null, CodeText(code));

            case ParagraphBlock paragraph:
                return new MdParagraph(ConvertInlines(paragraph.Inline, depth));

            case ListBlock list:
                var items = list.OfType<ListItemBlock>()
                    .Select(item => (IReadOnlyList<MdBlock>)ConvertBlocks(item, depth + 1))
                    .ToList();
                var start = list.IsOrdered && int.TryParse(list.OrderedStart, out var number) ? number : 1;
                return new MdList(list.IsOrdered, start, items);

            case QuoteBlock quote:
                return new MdQuote(ConvertBlocks(quote, depth + 1));

            case ThematicBreakBlock:
                return new MdRule();

            case Table table:
                var rows = table.OfType<TableRow>()
                    .Select(row => (IReadOnlyList<IReadOnlyList<MdInline>>)row.OfType<TableCell>()
                        .Select(cell => (IReadOnlyList<MdInline>)cell.OfType<ParagraphBlock>()
                            .SelectMany(p => ConvertInlines(p.Inline, depth + 1))
                            .ToList())
                        .ToList())
                    .ToList();
                return new MdTable(rows, table.OfType<TableRow>().FirstOrDefault()?.IsHeader == true);

            case ContainerBlock other:
                var children = ConvertBlocks(other, depth + 1);
                return children.Count == 0 ? null : new MdQuote(children);

            case LeafBlock leaf when leaf.Inline is not null:
                return new MdParagraph(ConvertInlines(leaf.Inline, depth));

            case LeafBlock leaf:
                var text = CodeText(leaf);
                return text.Length == 0 ? null : new MdParagraph([new MdText(text)]);

            default:
                return null;
        }
    }

    private static List<MdInline> ConvertInlines(ContainerInline? container, int depth)
    {
        var inlines = new List<MdInline>();
        if (container is null)
            return inlines;

        foreach (var inline in container)
        {
            if (ConvertInline(inline, depth) is { } converted)
                inlines.Add(converted);
        }

        return inlines;
    }

    private static MdInline? ConvertInline(MarkdigInline inline, int depth)
    {
        if (depth > MaxDepth)
            return new MdText(inline.ToString() ?? "");

        switch (inline)
        {
            case LiteralInline literal:
                return new MdText(literal.Content.ToString());

            case CodeInline code:
                return new MdCode(code.Content);

            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new MdLineBreak() : new MdText(" ");

            case AutolinkInline autolink:
                return new MdLink([new MdText(autolink.Url)], autolink.IsEmail ? null : SafeUrl(autolink.Url), autolink.Url);

            case LinkInline { IsImage: true } image:
                // Never download images from model output: show the description and the address instead.
                var alt = ToPlainText(ConvertInlines(image, depth + 1));
                var label = $"[image: {(alt.Length > 0 ? alt : "no description")}]";
                return new MdLink([new MdText(label)], SafeUrl(image.Url), image.Url ?? "");

            case LinkInline link:
                return new MdLink(ConvertInlines(link, depth + 1), SafeUrl(link.Url), link.Url ?? "");

            case EmphasisInline emphasis:
                var children = ConvertInlines(emphasis, depth + 1);
                return emphasis.DelimiterChar == '~'
                    ? new MdStrikethrough(children)
                    : emphasis.DelimiterCount >= 2 ? new MdStrong(children) : new MdEmphasis(children);

            case HtmlEntityInline entity:
                return new MdText(entity.Transcoded.ToString());

            case HtmlInline html:
                return new MdText(html.Tag);

            case ContainerInline container:
                var nested = ConvertInlines(container, depth + 1);
                return nested.Count == 0 ? null : new MdText(ToPlainText(nested));

            default:
                return new MdText(inline.ToString() ?? "");
        }
    }

    private static string CodeText(LeafBlock block)
    {
        var lines = block.Lines.Lines?.Take(block.Lines.Count).Select(l => l.ToString()) ?? [];
        return string.Join("\n", lines).TrimEnd('\n');
    }

    private static string RawText(MarkdigBlock block) => block is LeafBlock leaf ? CodeText(leaf) : "";
}
