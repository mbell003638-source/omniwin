using System.Globalization;
using System.Text;
using OmniWin.Core;

namespace OmniWin.Ocr;

/// <summary>
/// The text the model reads for <c>screen_ocr</c> (proposal Q7). First line is the untrusted-content header; then
/// <c>ocr target=… words=N lines=M total=…ms (phases)</c>; then one line per recognised line of text, each word as
/// <c>oN "text"</c>. Text only: no pixels, image data or file paths.
/// </summary>
public static class OcrFormat
{
    public static string Render(OcrCapture capture, Timing timing)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(timing);

        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder(ResultFormatter.UntrustedHeader).Append('\n')
            .Append("ocr target=").Append(capture.Target.Replace(' ', '_'))
            .Append(" words=").Append(capture.Result.Words.Count.ToString(inv))
            .Append(" lines=").Append(capture.Lines.Count.ToString(inv))
            .Append(' ');
        ResultFormatter.AppendTiming(text, timing);

        foreach (var line in capture.Lines)
        {
            text.Append('\n');
            var first = true;
            foreach (var word in line.Words)
            {
                if (!first) text.Append(' ');
                first = false;
                if (word.Id is not null) text.Append(word.Id).Append(' ');
                text.Append(ResultFormatter.Quote(word.Text, ResultFormatter.MaxNameChars));
            }
        }

        return text.ToString();
    }
}
