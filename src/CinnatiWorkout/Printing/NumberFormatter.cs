using System.Globalization;
using System.Text;
using CinnatiWorkout.Skinfold;

namespace CinnatiWorkout.Printing;

/// <summary>
/// Applica i formati numerici di Excel (es. "0.0", "#,##0.00", "0%", "d-mmm-yy") con le convenzioni italiane,
/// come li mostra Excel in italiano. Formati non riconosciuti ricadono su "Generale".
/// </summary>
public static class NumberFormatter
{
    static CultureInfo Culture => Num.Culture;

    public static string Format(double value, string format)
    {
        try
        {
            var sections = SplitSections(format);
            if (sections.Count == 0 || sections[0].Trim().Equals("General", StringComparison.OrdinalIgnoreCase)) return General(value);

            // Il formato di una data ha lettere d/m/y/h/s fuori dalle parti letterali.
            var section = value < 0 && sections.Count > 1 ? sections[1] : value == 0 && sections.Count > 2 ? sections[2] : sections[0];
            var cleaned = Clean(section);
            if (IsDateFormat(cleaned))
                return FormatDate(DateTime.FromOADate(value), cleaned);

            // .NET gestisce le stesse sezioni (positivi;negativi;zero) e gli stessi segnaposto 0 # . , % E+.
            var numeric = string.Join(";", sections.Take(3).Select(s => Clean(s, forNet: true)));
            if (numeric.Contains('?') || numeric.Contains('/')) return General(value);
            if (numeric.Trim().Equals("General", StringComparison.OrdinalIgnoreCase)) return General(value);
            return value.ToString(numeric, Culture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return General(value);
        }
    }

    /// <summary>Formato "Generale": fino a 10 cifre significative, notazione scientifica per valori estremi.</summary>
    public static string General(double value)
    {
        if (value == 0) return "0";
        double abs = Math.Abs(value);
        if (abs >= 1e11 || abs < 1e-9) return value.ToString("0.#####E+00", Culture);
        var rounded = double.Parse(value.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded.ToString("0.##########", Culture);
    }

    static List<string> SplitSections(string format)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, bracket = false;
        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch == '\\' && i + 1 < format.Length && !quoted) { current.Append(ch).Append(format[++i]); continue; }
            if (ch == '"' && !bracket) quoted = !quoted;
            else if (ch == '[' && !quoted) bracket = true;
            else if (ch == ']' && !quoted) bracket = false;
            else if (ch == ';' && !quoted && !bracket) { result.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Toglie colori, condizioni e impostazioni locali tra parentesi quadre ([Red], [$-410]),
    /// tiene i simboli di valuta ([$€-410] → "€"), converte _x (spazio) e *x (riempimento).
    /// </summary>
    static string Clean(string section, bool forNet = false)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < section.Length; i++)
        {
            char ch = section[i];
            if (quoted)
            {
                sb.Append(ch);
                if (ch == '"') quoted = false;
                continue;
            }
            switch (ch)
            {
                case '"':
                    quoted = true;
                    sb.Append(ch);
                    break;
                case '\\' when i + 1 < section.Length:
                    sb.Append(ch).Append(section[++i]);
                    break;
                case '[':
                    int end = section.IndexOf(']', i);
                    if (end < 0) { i = section.Length; break; }
                    var inner = section[(i + 1)..end];
                    if (inner.StartsWith('$'))
                    {
                        var symbol = inner[1..].Split('-')[0];
                        if (symbol.Length > 0) sb.Append('"').Append(symbol).Append('"');
                    }
                    else if (inner.Length > 0 && "hms".Contains(char.ToLowerInvariant(inner[0])))
                        sb.Append(inner); // ore/minuti trascorsi: si trattano come normali
                    i = end;
                    break;
                case '_' when i + 1 < section.Length:
                    sb.Append(' ');
                    i++;
                    break;
                case '*' when i + 1 < section.Length:
                    i++;
                    break;
                case '@' when forNet:
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    static bool IsDateFormat(string format)
    {
        bool quoted = false;
        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch == '\\') { i++; continue; }
            if ("dDmMyYhHsS".Contains(ch)) return true;
        }
        return false;
    }

    static string FormatDate(DateTime date, string format)
    {
        // Suddivide in elementi: sequenze della stessa lettera, AM/PM, letterali.
        var tokens = new List<(string Text, bool Literal)>();
        bool ampm = format.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || format.Contains("A/P", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < format.Length;)
        {
            char ch = format[i];
            if (ch == '"')
            {
                int end = format.IndexOf('"', i + 1);
                if (end < 0) end = format.Length;
                tokens.Add((format[(i + 1)..Math.Min(end, format.Length)], true));
                i = end + 1;
            }
            else if (ch == '\\' && i + 1 < format.Length) { tokens.Add((format[i + 1].ToString(), true)); i += 2; }
            else if (string.Compare(format, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { tokens.Add(("AM/PM", false)); i += 5; }
            else if (string.Compare(format, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) { tokens.Add(("A/P", false)); i += 3; }
            else if ("dmyhs".Contains(char.ToLowerInvariant(ch)))
            {
                int j = i;
                while (j < format.Length && char.ToLowerInvariant(format[j]) == char.ToLowerInvariant(ch)) j++;
                tokens.Add((format[i..j].ToLowerInvariant(), false));
                i = j;
            }
            else if (ch == '0' && tokens.Count > 0 && tokens[^1].Text.StartsWith('s'))
            {
                int j = i;
                while (j < format.Length && format[j] == '0') j++;
                tokens.Add((new string('0', j - i), false));
                i = j;
            }
            else { tokens.Add((ch.ToString(), true)); i++; }
        }

        var sb = new StringBuilder();
        var dtf = Culture.DateTimeFormat;
        for (int k = 0; k < tokens.Count; k++)
        {
            var (text, literal) = tokens[k];
            if (literal) { sb.Append(text); continue; }
            switch (text[0])
            {
                case 'y':
                    sb.Append(text.Length <= 2 ? (date.Year % 100).ToString("00") : date.Year.ToString("0000"));
                    break;
                case 'd':
                    sb.Append(text.Length switch
                    {
                        1 => date.Day.ToString(),
                        2 => date.Day.ToString("00"),
                        3 => dtf.GetAbbreviatedDayName(date.DayOfWeek),
                        _ => dtf.GetDayName(date.DayOfWeek),
                    });
                    break;
                case 'm':
                    if (IsMinute(tokens, k))
                        sb.Append(text.Length == 1 ? date.Minute.ToString() : date.Minute.ToString("00"));
                    else
                        sb.Append(text.Length switch
                        {
                            1 => date.Month.ToString(),
                            2 => date.Month.ToString("00"),
                            3 => dtf.GetAbbreviatedMonthName(date.Month).TrimEnd('.'),
                            5 => dtf.GetMonthName(date.Month)[..1],
                            _ => dtf.GetMonthName(date.Month),
                        });
                    break;
                case 'h':
                    int hour = ampm ? (date.Hour % 12 == 0 ? 12 : date.Hour % 12) : date.Hour;
                    sb.Append(text.Length == 1 ? hour.ToString() : hour.ToString("00"));
                    break;
                case 's':
                    sb.Append(text.Length == 1 ? date.Second.ToString() : date.Second.ToString("00"));
                    break;
                case '0':
                    sb.Append(date.Millisecond.ToString("000")[..Math.Min(3, text.Length)]);
                    break;
                case 'A':
                    sb.Append(text == "A/P" ? (date.Hour < 12 ? "A" : "P") : (date.Hour < 12 ? "AM" : "PM"));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>"m" vale minuti se segue un'ora o precede dei secondi, altrimenti è il mese.</summary>
    static bool IsMinute(List<(string Text, bool Literal)> tokens, int index)
    {
        if (tokens[index].Text.Length > 2) return false;
        for (int i = index - 1; i >= 0; i--)
            if (!tokens[i].Literal) { if (tokens[i].Text[0] == 'h') return true; break; }
        for (int i = index + 1; i < tokens.Count; i++)
            if (!tokens[i].Literal) return tokens[i].Text[0] == 's';
        return false;
    }
}
