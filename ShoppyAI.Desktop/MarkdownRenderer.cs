using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ShoppyAI.Core.Markdown;
using ShoppyAI.Core.Text;

namespace ShoppyAI.Desktop;

/// <summary>
/// Renders model answers (untrusted Markdown) with WPF elements only: nothing is executed
/// or downloaded, links open in the browser only after confirmation, and every paragraph
/// gets its own direction so Arabic, French and English can be mixed.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush TextBrush = Brushes.White;
    private static readonly Brush MutedBrush = ParseBrush("#D1D5DB");
    private static readonly Brush LinkBrush = ParseBrush("#60A5FA");
    private static readonly Brush CodeBackground = ParseBrush("#0B1220");
    private static readonly Brush InlineCodeBackground = ParseBrush("#1F2937");
    private static readonly Brush BorderBrush = ParseBrush("#374151");
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    public static UIElement Render(string markdown)
    {
        var panel = new StackPanel();
        foreach (var block in SafeMarkdown.Parse(markdown))
            panel.Children.Add(RenderBlock(block));

        return panel;
    }

    /// <summary>Right-to-left for mostly Arabic text, left-to-right otherwise.</summary>
    public static void ApplyDirection(FrameworkElement element, string text)
    {
        element.FlowDirection = TextDirection.IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private static UIElement RenderBlock(MdBlock block) => block switch
    {
        MdParagraph paragraph => Paragraph(paragraph.Inlines, 15, FontWeights.Normal),
        MdHeading heading => Paragraph(heading.Inlines, heading.Level switch { 1 => 21, 2 => 19, _ => 17 }, FontWeights.SemiBold),
        MdCodeBlock code => CodeBlock(code),
        MdList list => List(list),
        MdQuote quote => Quote(quote),
        MdTable table => Table(table),
        MdRule => new Border { Height = 1, Background = BorderBrush, Margin = new Thickness(0, 8, 0, 8) },
        _ => new TextBlock()
    };

    private static TextBlock Paragraph(IReadOnlyList<MdInline> inlines, double fontSize, FontWeight weight)
    {
        var text = new TextBlock
        {
            Foreground = TextBrush,
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        };

        foreach (var inline in inlines)
            text.Inlines.Add(RenderInline(inline));

        ApplyDirection(text, SafeMarkdown.ToPlainText(inlines));
        return text;
    }

    private static Inline RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case MdText text:
                return new Run(text.Text);

            case MdCode code:
                return new Run(code.Code)
                {
                    FontFamily = CodeFont,
                    Background = InlineCodeBackground,
                    FlowDirection = FlowDirection.LeftToRight
                };

            case MdLineBreak:
                return new LineBreak();

            case MdStrong strong:
                return Span(new Bold(), strong.Children);

            case MdEmphasis emphasis:
                return Span(new Italic(), emphasis.Children);

            case MdStrikethrough strike:
                var struck = Span(new Span(), strike.Children);
                struck.TextDecorations = TextDecorations.Strikethrough;
                return struck;

            case MdLink { Url: { } url } link:
                var hyperlink = Span(new Hyperlink { Foreground = LinkBrush, ToolTip = url.AbsoluteUri }, link.Children);
                ((Hyperlink)hyperlink).Click += (_, _) => OpenLink(url);
                return hyperlink;

            case MdLink link:
                // Not an http(s) address: show it, never open it.
                var label = SafeMarkdown.ToPlainText(link.Children);
                var target = string.IsNullOrWhiteSpace(link.RawTarget) || label == link.RawTarget ? "" : $" ({link.RawTarget})";
                return new Run(label + target) { Foreground = MutedBrush };

            default:
                return new Run();
        }
    }

    private static Span Span(Span span, IReadOnlyList<MdInline> children)
    {
        foreach (var child in children)
            span.Inlines.Add(RenderInline(child));

        return span;
    }

    private static UIElement CodeBlock(MdCodeBlock code)
    {
        var box = new TextBox
        {
            Text = code.Code,
            IsReadOnly = true,
            FontFamily = CodeFont,
            FontSize = 13,
            Foreground = ParseBrush("#E5E7EB"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Thickness(4)
        };

        // The code box must not swallow the mouse wheel of the conversation.
        box.PreviewMouseWheel += ForwardMouseWheel;

        return new Border
        {
            Background = CodeBackground,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 2, 0, 10),
            FlowDirection = FlowDirection.LeftToRight,
            Child = box
        };
    }

    private static UIElement List(MdList list)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < list.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var marker = new TextBlock
            {
                Text = list.Ordered ? $"{list.Start + i}." : "•",
                Foreground = MutedBrush,
                FontSize = 15,
                Margin = new Thickness(4, 2, 8, 0)
            };
            Grid.SetRow(marker, i);
            grid.Children.Add(marker);

            var item = new StackPanel();
            foreach (var block in list.Items[i])
                item.Children.Add(RenderBlock(block));

            Grid.SetRow(item, i);
            Grid.SetColumn(item, 1);
            grid.Children.Add(item);
        }

        ApplyDirection(grid, string.Concat(list.Items.SelectMany(b => b).OfType<MdParagraph>().Select(p => SafeMarkdown.ToPlainText(p.Inlines))));
        return grid;
    }

    private static UIElement Quote(MdQuote quote)
    {
        var panel = new StackPanel();
        foreach (var block in quote.Blocks)
            panel.Children.Add(RenderBlock(block));

        return new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel
        };
    }

    private static UIElement Table(MdTable table)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                var cell = Paragraph(table.Rows[r][c], 14, r == 0 && table.HasHeader ? FontWeights.SemiBold : FontWeights.Normal);
                cell.Margin = new Thickness(0);
                var border = new Border
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = cell
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid
        };
    }

    private static void OpenLink(Uri url)
    {
        var answer = MessageBox.Show(
            $"Open this address in your browser?\n\n{url.AbsoluteUri}",
            "ShoppyAI",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    private static void ForwardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element)
            return;

        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(element) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        });
    }

    private static SolidColorBrush ParseBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
