using System.Text;
using System.Text.Json;
using Hilde.Core;

namespace Hilde.Desktop;

internal static class Exporter
{
    public static string Render(TranscriptRecord r, string format)
    {
        var local = r.CreatedUtc.ToLocalTime();
        switch (format)
        {
            case "json":
                return JsonSerializer.Serialize(new
                {
                    r.Id, r.Title, created = local, r.Language, r.Mode, r.Status, r.DurationMs, r.Source,
                    finalText = r.FinalText, cleanText = r.CleanText, rawText = r.RawText
                }, new JsonSerializerOptions(UiBridge.Json) { WriteIndented = true });
            case "md":
                var sb = new StringBuilder();
                sb.AppendLine($"# {r.Title}").AppendLine();
                sb.AppendLine($"*{local:dd.MM.yyyy HH:mm} · {r.Language} · {r.Mode}*").AppendLine();
                sb.AppendLine(r.FinalText).AppendLine();
                if (r.RawText != r.FinalText)
                {
                    sb.AppendLine("## Originaltranskript").AppendLine();
                    sb.AppendLine(r.RawText);
                }
                return sb.ToString();
            default:
                return r.FinalText;
        }
    }
}
