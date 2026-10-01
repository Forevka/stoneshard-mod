using System.Text;
using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The action log at the bottom left, as plain text.
/// </summary>
/// <remarks>
/// o_actionsLog.messagesList is a ds_list with one entry per message: an array
/// [map, map, alpha] of two renderings (normal and enlarged) of the same line.
/// Each map's "textArray" holds the coloured pieces as [text, colour, x, row];
/// joining the texts gives the line. A message keeps its maps while it is
/// listed, so their ids tell new lines from ones already seen.
/// </remarks>
internal static class ActionLog
{
    public readonly record struct Line(long Key, string Text);

    /// <summary>Every listed line, oldest first.</summary>
    public static List<Line> Read()
    {
        var lines = new List<Line>();
        if (Objects.o_actionsLog.First is not { } log) return lines;
        var list = new DsList(log.Get("messagesList"));
        if (!list.Exists) return lines;
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            var entry = list.At(i);
            if (Gml.TypeOf(entry) != "array" || Gml.ArrayLength(entry) < 1) continue;
            var mapId = Gml.ArrayGet(entry, 0);
            var map = new DsMap(mapId);
            if (!map.Exists) continue;
            lines.Add(new Line(Gm.IdKey(mapId), Text(map.Get("textArray"))));
        }
        // The list is kept newest first (a new message goes in at the top).
        if (lines.Count > 1 && Newest(lines[0].Key, lines[^1].Key)) lines.Reverse();
        return lines;
    }

    // Map ids grow as messages are made, so a larger first id means newest first.
    private static bool Newest(long first, long last) => first > last;

    private static string Text(RValue pieces)
    {
        if (Gml.TypeOf(pieces) != "array") return "";
        var sb = new StringBuilder();
        int n = Gml.ArrayLength(pieces);
        double row = 0;
        for (int i = 0; i < n; i++)
        {
            var piece = Gml.ArrayGet(pieces, i);
            if (Gml.TypeOf(piece) != "array" || Gml.ArrayLength(piece) < 1) continue;
            double r = Gm.ArrNum(piece, 3, row);
            // A wrapped line continues on the next row: join with a space.
            if (r != row && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            row = r;
            var text = Gml.ArrayGet(piece, 0);
            if (text.Kind == RValueKind.String) sb.Append(text.ToString());
        }
        return sb.ToString().Trim();
    }

    /// <summary>The lines listed now, to find the new ones later.</summary>
    public static Dictionary<long, string> Snapshot()
    {
        var seen = new Dictionary<long, string>();
        foreach (var l in Read()) seen[l.Key] = l.Text;
        return seen;
    }

    /// <summary>
    /// Lines added since the snapshot, and lines whose text changed: a repeated
    /// message is not added again but counted on its own line ("... [x4]").
    /// </summary>
    public static List<string> Since(Dictionary<long, string> seen) =>
        Read().Where(l => !seen.TryGetValue(l.Key, out var before) || before != l.Text).Select(l => l.Text).ToList();
}
