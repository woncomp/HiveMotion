using System;
using System.Collections.Generic;
using Point = System.Windows.Point;

namespace HiveMotion;

/// <summary>Keyboard-shaped hex grid geometry, mirroring the design's data/tasks.ts constants.</summary>
public static class KeyGrid
{
    public const double HexW = 132;
    public const double HexH = 152;
    public const double PitchX = HexW + 10;
    public const double PitchY = HexH * 0.75 + 10;
    public const double GridW = PitchX * 9 + HexW;
    public const double GridH = PitchY * 2 + HexH;

    public static readonly char[][] Rows =
    {
        "QWERTYUIOP".ToCharArray(),
        "ASDFGHJKL".ToCharArray(),
        "ZXCVBNM".ToCharArray()
    };

    private static readonly double[] RowOffset = { 0, PitchX * 0.5, PitchX * 1.0 };

    private static readonly Dictionary<char, Point> Centers = BuildCenters();
    private static readonly Dictionary<char, IReadOnlyList<char>> NeighborOrder = BuildNeighborOrder();

    /// <summary>X of the cut line between G and H; cells left/right of it slide apart while searching.</summary>
    public static readonly double CutX = (Centers['G'].X + Centers['H'].X) / 2;

    public static IReadOnlyList<char> AllLetters { get; } = BuildAllLetters();

    public static Point CenterOf(char letter) => Centers[letter];

    /// <summary>Letters sorted by distance from <paramref name="target"/> (nearest first, target itself first).</summary>
    public static IReadOnlyList<char> ByDistanceFrom(char target) => NeighborOrder[target];

    private static List<char> BuildAllLetters()
    {
        var all = new List<char>(26);
        foreach (var row in Rows)
            all.AddRange(row);
        return all;
    }

    private static Dictionary<char, Point> BuildCenters()
    {
        var centers = new Dictionary<char, Point>();
        for (int row = 0; row < Rows.Length; row++)
        {
            for (int col = 0; col < Rows[row].Length; col++)
            {
                centers[Rows[row][col]] = new Point(
                    RowOffset[row] + col * PitchX + HexW / 2,
                    row * PitchY + HexH / 2);
            }
        }
        return centers;
    }

    private static Dictionary<char, IReadOnlyList<char>> BuildNeighborOrder()
    {
        var order = new Dictionary<char, IReadOnlyList<char>>();
        foreach (var pair in Centers)
        {
            var origin = pair.Value;
            var sorted = new List<char>(Centers.Keys);
            sorted.Sort((a, b) =>
            {
                double da = (Centers[a] - origin).LengthSquared;
                double db = (Centers[b] - origin).LengthSquared;
                int cmp = da.CompareTo(db);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            order[pair.Key] = sorted;
        }
        return order;
    }
}
