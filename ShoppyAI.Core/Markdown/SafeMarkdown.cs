using Markdig;
using Markdig.Extensions.EmphasisExtras;
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
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
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

    /// <summary>Plain text of inlines, e.g. for accessibility.</summary>
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

    /// <summary>
    /// The prose of a paragraph, without inline code or bare addresses, to choose its
    /// direction: an Arabic sentence with a long command in it stays right-to-left.
    /// </summary>
    public static string ToDirectionText(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdCode => " ",
        MdLineBreak => "\n",
        MdStrong s => ToDirectionText(s.Children),
        MdEmphasis e => ToDirectionText(e.Children),
        MdStrikethrough s => ToDirectionText(s.Children),
        MdLink l when ToPlainText(l.Children) == l.RawTarget => " ",
        MdLink l => ToDirectionText(l.Children),
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
        {
            var flattened = FlattenBlock(block);
            return flattened.Length == 0 ? null : new MdParagraph([new MdText(flattened)]);
        }

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
            if (depth > MaxDepth)
            {
                inlines.Add(new MdText(FlattenInline(inline)));
            }
            else if (inline is DelimiterInline delimiter)
            {
                // Unmatched "[", "*"…: keep the character and what follows it.
                inlines.Add(new MdText(delimiter.ToLiteral()));
                inlines.AddRange(ConvertInlines(delimiter, depth + 1));
            }
            else if (ConvertInline(inline, depth) is { } converted)
            {
                inlines.Add(converted);
            }
        }

        return inlines;
    }

    private static MdInline? ConvertInline(MarkdigInline inline, int depth)
    {
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
                // A link inside a link (e.g. a badge image) becomes text: UI frameworks reject nested links.
                return new MdLink(WithoutLinks(ConvertInlines(link, depth + 1)), SafeUrl(link.Url), link.Url ?? "");

            case EmphasisInline emphasis:
                var children = ConvertInlines(emphasis, depth + 1);
                return emphasis.DelimiterChar switch
                {
                    '~' when emphasis.DelimiterCount >= 2 => new MdStrikethrough(children),
                    '*' or '_' when emphasis.DelimiterCount >= 2 => new MdStrong(children),
                    '*' or '_' => new MdEmphasis(children),
                    _ => new MdText(new string(emphasis.DelimiterChar, emphasis.DelimiterCount) + ToPlainText(children) +
                                    new string(emphasis.DelimiterChar, emphasis.DelimiterCount))
                };

            case HtmlEntityInline entity:
                return new MdText(entity.Transcoded.ToString());

            case HtmlInline html:
                return new MdText(html.Tag);

            case ContainerInline container:
                var nested = ConvertInlines(container, depth + 1);
                return nested.Count == 0 ? null : new MdText(ToPlainText(nested));

            default:
                return new MdText(FlattenInline(inline));
        }
    }

    private static List<MdInline> WithoutLinks(IEnumerable<MdInline> inlines) => inlines.Select(i => i switch
    {
        MdLink l => new MdText(ToPlainText(l.Children)),
        MdStrong s => new MdStrong(WithoutLinks(s.Children)),
        MdEmphasis e => new MdEmphasis(WithoutLinks(e.Children)),
        MdStrikethrough s => new MdStrikethrough(WithoutLinks(s.Children)),
        _ => i
    }).ToList();

    /// <summary>Text of an inline and its children, used past the nesting limit.</summary>
    private static string FlattenInline(MarkdigInline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        LineBreakInline => " ",
        AutolinkInline autolink => autolink.Url,
        HtmlEntityInline entity => entity.Transcoded.ToString(),
        HtmlInline html => html.Tag,
        DelimiterInline delimiter => delimiter.ToLiteral() + string.Concat(delimiter.Select(FlattenInline)),
        ContainerInline container => string.Concat(container.Select(FlattenInline)),
        _ => ""
    };

    /// <summary>Text of a block and its children, used past the nesting limit.</summary>
    private static string FlattenBlock(MarkdigBlock block) => block switch
    {
        LeafBlock { Inline: { } inline } => FlattenInline(inline),
        LeafBlock leaf => CodeText(leaf),
        ContainerBlock container => string.Join("\n", container.Select(FlattenBlock).Where(t => t.Length > 0)),
        _ => ""
    };

    private static string CodeText(LeafBlock block)
    {
        var lines = block.Lines.Lines?.Take(block.Lines.Count).Select(l => l.ToString()) ?? [];
        return string.Join("\n", lines).TrimEnd('\n');
    }
}
