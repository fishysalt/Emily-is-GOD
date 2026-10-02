using System.Text.RegularExpressions;

namespace ValleyTalkMemory;

/// <summary>
/// Strips Stardew Valley dialogue markup out of remembered lines.
///
/// Generated lines arrive with presentation tokens attached: <c>#$b#</c>/<c>#$e#</c> page breaks,
/// <c>$q</c> question blocks, <c>$r</c> response options (the whole answers UI), emotion tokens such
/// as <c>$h</c>, and <c>[item id]</c> gift options. Remembering those verbatim both pollutes the
/// model's context and burns the character budget, so they are removed before storage.
/// </summary>
internal static class DialogueTextCleaner
{
    private static readonly Regex PageBreaks = new Regex(@"#\$(?:b|e)#", RegexOptions.Compiled);
    private static readonly Regex QuestionBlock = new Regex(@"\s*\$q\s.*$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ResponseBlock = new Regex(@"\s*\$r\s.*$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EmotionToken = new Regex(@"\$[a-zA-Z0-9]", RegexOptions.Compiled);
    private static readonly Regex GiftOptions = new Regex(@"\s*\[[^\[\]]{1,80}\]\s*", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new Regex(@"[ \t\u00a0]{2,}", RegexOptions.Compiled);

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string result = text;

        // Order matters: page breaks turn into spaces, then the trailing UI blocks are cut.
        result = PageBreaks.Replace(result, " ");
        result = QuestionBlock.Replace(result, "");
        result = ResponseBlock.Replace(result, "");
        result = EmotionToken.Replace(result, " ");
        result = GiftOptions.Replace(result, " ");
        result = Whitespace.Replace(result, " ");
        result = result.Replace("#$", "$");

        result = result.Trim();
        result = result.TrimStart('-', '%', ' ');
        result = result.TrimEnd('#');

        // Generated lines occasionally end with a stray brace (a truncated template/JSON artifact).
        result = result.TrimEnd('{', '}', ' ');

        return result.Trim();
    }
}
