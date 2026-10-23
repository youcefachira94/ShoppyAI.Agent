namespace ShoppyAI.Core.Text;

/// <summary>
/// Chooses the base direction of a paragraph. Chat messages often mix Arabic with
/// French or English technical words, so the majority script decides; the first
/// strong character breaks ties (like HTML dir="auto"). Code and web addresses are
/// ignored: they are always left-to-right and would flip short Arabic sentences.
/// </summary>
public static class TextDirection
{
    public static bool IsRightToLeft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = TextNormalizer.StripCodeAndUrls(text);
        var rtl = 0;
        var ltr = 0;
        bool? firstStrongIsRtl = null;

        foreach (var c in text)
        {
            if (IsRtlLetter(c))
            {
                rtl++;
                firstStrongIsRtl ??= true;
            }
            else if (char.IsLetter(c))
            {
                ltr++;
                firstStrongIsRtl ??= false;
            }
        }

        if (rtl == ltr)
            return firstStrongIsRtl ?? false;

        return rtl > ltr;
    }

    private static bool IsRtlLetter(char c) =>
        TextNormalizer.IsArabicLetter(c) || c is >= '֐' and <= '׿';
}
