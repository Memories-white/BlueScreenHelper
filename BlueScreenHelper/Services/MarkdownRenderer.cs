using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BlueScreenHelper.Services;

public static partial class MarkdownRenderer
{
    private const string InlineTokenPattern = @"(\*\*.*?\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))";
    private static readonly Regex InlineTokenRegex = new(InlineTokenPattern, RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^\d+[.、]\s+(.*)$", RegexOptions.Compiled);

    public static UIElement Render(string? markdown)
    {
        var root = new StackPanel { Spacing = 6 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return root;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inCodeBlock = false;
        var codeBuf = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    AddCodeBlock(root, codeBuf.ToString());
                    codeBuf.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBuf.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("### "))
            {
                AddHeading(root, line[4..], 18);
                continue;
            }
            if (line.StartsWith("## "))
            {
                AddHeading(root, line[3..], 20);
                continue;
            }
            if (line.StartsWith("# "))
            {
                AddHeading(root, line[2..], 22);
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                root.Children.Add(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(0, 2, 0, 2),
                    Fill = new SolidColorBrush(GetThemeColor("CardStrokeColorDefaultBrush", 0x2AFFFFFF))
                });
                continue;
            }

            if (line.StartsWith('>'))
            {
                AddQuote(root, line.TrimStart('>').Trim());
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                AddListItem(root, "•", line[2..].TrimStart());
                continue;
            }

            var ordered = OrderedListRegex.Match(line);
            if (ordered.Success)
            {
                var num = line[..line.IndexOf('.')];
                AddListItem(root, num, ordered.Groups[1].Value);
                continue;
            }

            AddParagraph(root, line);
        }

        if (inCodeBlock)
        {
            AddCodeBlock(root, codeBuf.ToString());
        }

        return root;
    }

    private static void AddHeading(StackPanel root, string text, double fontSize)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0)
        };
        AddInlines(tb, text);
        root.Children.Add(tb);
    }

    private static void AddParagraph(StackPanel root, string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        AddInlines(tb, text);
        root.Children.Add(tb);
    }

    private static void AddListItem(StackPanel root, string marker, string text)
    {
        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new TextBlock { Text = marker, FontWeight = FontWeights.SemiBold, Foreground = GetAccentBrush() };
        Grid.SetColumn(mark, 0);
        panel.Children.Add(mark);
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        AddInlines(tb, text);
        Grid.SetColumn(tb, 1);
        panel.Children.Add(tb);
        root.Children.Add(panel);
    }

    private static void AddQuote(StackPanel root, string text)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(GetThemeColor("SubtleFillColorSecondaryBrush", 0x14FFFFFF))
        };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21, Opacity = 0.9 };
        AddInlines(tb, text);
        border.Child = tb;
        root.Children.Add(border);
    }

    private static void AddCodeBlock(StackPanel root, string code)
    {
        var border = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(GetThemeColor("LayerOnAcrylicFillColorDefaultBrush", 0x0DFFFFFF)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        border.Child = new TextBlock
        {
            Text = code.TrimEnd('\n'),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            LineHeight = 18
        };
        root.Children.Add(border);
    }

    private static void AddInlines(TextBlock tb, string text)
    {
        foreach (var token in InlineTokenRegex.Split(text))
        {
            if (token.Length == 0)
            {
                continue;
            }
            if (token.StartsWith("**") && token.EndsWith("**") && token.Length > 4)
            {
                tb.Inlines.Add(new Run { Text = token[2..^2], FontWeight = FontWeights.SemiBold });
            }
            else if (token.StartsWith('`') && token.EndsWith('`') && token.Length > 2)
            {
                tb.Inlines.Add(new Run
                {
                    Text = token[1..^1],
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    Foreground = GetAccentBrush()
                });
            }
            else if (token.StartsWith('[') && token.Contains("](", StringComparison.Ordinal) && token.EndsWith(')'))
            {
                var mid = token.IndexOf("](", StringComparison.Ordinal);
                var label = token[1..mid];
                var url = token[(mid + 2)..^1];
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    tb.Inlines.Add(new Hyperlink { NavigateUri = uri, Inlines = { new Run { Text = label } } });
                }
                else
                {
                    tb.Inlines.Add(new Run { Text = token });
                }
            }
            else
            {
                tb.Inlines.Add(new Run { Text = token });
            }
        }
    }

    private static Brush GetAccentBrush()
    {
        try
        {
            return (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        }
        catch
        {
            return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        }
    }

    private static Color GetThemeColor(string key, uint fallbackArgb)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush b)
            {
                return b.Color;
            }
        }
        catch
        {
        }
        return Color.FromArgb(
            (byte)(fallbackArgb >> 24), (byte)(fallbackArgb >> 16), (byte)(fallbackArgb >> 8), (byte)fallbackArgb);
    }
}
