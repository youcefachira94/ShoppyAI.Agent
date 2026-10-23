using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShoppyAI.Core.Text;

/// <summary>
/// Tokenization and spelling normalization for Darija written in Arabic script or in
/// Latin letters (Arabizi), French and English.
/// </summary>
public static partial class TextNormalizer
{
    private const string Vowels = "aeiouy";

    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}]+")]
    private static partial Regex WordRegex();

    /// <summary>Removes code blocks, inline code and URLs, which carry no language signal.</summary>
    public static string StripCodeAndUrls(string text)
    {
        var withoutBlocks = FencedCodeRegex().Replace(text, " ");
        var withoutInline = InlineCodeRegex().Replace(withoutBlocks, " ");
        return UrlRegex().Replace(withoutInline, " ");
    }

    /// <summary>
    /// Splits text into words. A word that mixes Arabic and Latin letters (for example
    /// "نconnecti") is split at the script boundary.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();

        foreach (Match match in WordRegex().Matches(text))
        {
            var word = match.Value;
            var start = 0;

            for (var i = 1; i < word.Length; i++)
            {
                if (IsArabicLetter(word[i]) != IsArabicLetter(word[i - 1]) &&
                    char.IsLetter(word[i]) && char.IsLetter(word[i - 1]))
                {
                    tokens.Add(word[start..i]);
                    start = i;
                }
            }

            tokens.Add(word[start..]);
        }

        return tokens;
    }

    public static bool IsArabicLetter(char c) =>
        char.IsLetter(c) &&
        (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ࢠ' and <= 'ࣿ' or
            >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿');

    public static bool IsLatinLetter(char c) =>
        char.IsLetter(c) && c <= 'ɏ';

    public static bool ContainsArabic(string token) => token.Any(IsArabicLetter);

    /// <summary>Lower case, accents removed. Used for French and English words.</summary>
    public static string PlainKey(string token)
    {
        var decomposed = token.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Key used to compare Darija words whatever the spelling: Arabic letter variants are
    /// unified, and Arabizi variants (7/h, 5/kh, q/9, sh/ch, ou/u/o, doubled letters) collapse.
    /// </summary>
    public static string DarijaKey(string token)
    {
        if (ContainsArabic(token))
            return NormalizeArabic(token);

        var key = PlainKey(token);
        var hasLetter = key.Any(char.IsLetter);
        var hasDigit = key.Any(char.IsDigit);

        if (hasLetter && hasDigit)
        {
            key = key
                .Replace("7", "h")
                .Replace("5", "kh")
                .Replace("8", "gh")
                .Replace("6", "t");
        }

        key = key
            .Replace("q", "9")
            .Replace("sh", "ch")
            .Replace("ou", "o")
            .Replace("u", "o");

        return CollapseRepeats(key, minVowelRun: 3);
    }

    /// <summary>Normalizes Arabic script: diacritics, tatweel and letter variants.</summary>
    public static string NormalizeArabic(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
                continue;

            builder.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' or 'ئ' or 'ی' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ڨ' or 'گ' => 'ق',
                'ڤ' => 'ف',
                'ک' => 'ك',
                _ => c
            });
        }

        // Stretched words ("بزااااف") keep a single letter.
        var collapsed = new StringBuilder(builder.Length);
        for (var i = 0; i < builder.Length; i++)
        {
            var run = 1;
            while (i + run < builder.Length && builder[i + run] == builder[i])
                run++;

            collapsed.Append(builder[i], run >= 3 ? 1 : run);
            i += run - 1;
        }

        return collapsed.ToString();
    }

    /// <summary>
    /// Candidate lookup keys for an Arabic-script word: the word itself and the word
    /// without the attached conjunction "و" and/or article "ال".
    /// </summary>
    public static IEnumerable<string> ArabicVariants(string normalizedWord)
    {
        yield return normalizedWord;

        var rest = normalizedWord;
        if (rest.StartsWith('و') && rest.Length > 3)
        {
            rest = rest[1..];
            yield return rest;
        }

        if (rest.StartsWith("ال") && rest.Length > 4)
            yield return rest[2..];
    }

    /// <summary>
    /// The verb inside a Darija negation: "ما يخدمش" / "مايخدمش" give "يخدم",
    /// "ma ykhdemch" / "mafhemtch" give "ykhdem" / "fhemt". Takes and returns Darija keys.
    /// </summary>
    public static IEnumerable<string> NegationStems(string key, string? previousKey)
    {
        var (prefix, suffix) = key.Any(IsArabicLetter) ? ("ما", "ش") : ("ma", "ch");

        if (key.StartsWith(prefix) && key.EndsWith(suffix) && key.Length >= prefix.Length + suffix.Length + 3)
            yield return key[prefix.Length..^suffix.Length];
        else if (previousKey == prefix && key.EndsWith(suffix) && key.Length >= suffix.Length + 3)
            yield return key[..^suffix.Length];
    }

    private static string CollapseRepeats(string value, int minVowelRun)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var run = 1;
            while (i + run < value.Length && value[i + run] == c)
                run++;

            var isVowel = Vowels.Contains(c);
            var collapse = isVowel ? run >= minVowelRun : run >= 2 && char.IsLetter(c);
            builder.Append(c, collapse ? 1 : run);
            i += run - 1;
        }

        return builder.ToString();
    }
}
