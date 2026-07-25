using System;
using System.Collections.Generic;
using System.Linq;

namespace HiveMotion;

/// <summary>
/// Places running windows onto the keyboard hex grid.
/// Decision chain: pinned apps reserve their letter first and only accept windows of the
/// same program with the same arguments; then windows in user-priority order grab the
/// cell matching their app's initial; losers take the nearest free cell around the one
/// they wanted (second tier), after all exact matches are done.
/// </summary>
public sealed class CellAssigner
{
    private readonly IReadOnlyList<PinnedApp> _pins;

    public CellAssigner(IReadOnlyList<PinnedApp> pins)
    {
        _pins = pins;
    }

    public IReadOnlyList<HiveCell> Assign(IReadOnlyList<RunningWindow> windows)
    {
        var cells = new Dictionary<char, HiveCell>();
        var placed = new HashSet<RunningWindow>();

        // 1. Pinned apps reserve their configured letter first.
        foreach (var pin in _pins)
        {
            var cell = new HiveCell
            {
                Letter = pin.Key,
                Pin = pin,
                AppName = pin.DisplayName,
                Title = pin.DisplayName
            };

            // Scan order is z-order, so the first match is the topmost matching window.
            var match = windows.FirstOrDefault(w => !placed.Contains(w) && pin.Matches(w));
            if (match != null)
            {
                cell.WindowHandle = match.Handle;
                cell.ProcessId = match.ProcessId;
                cell.ProcessCreationFileTime = match.ProcessCreationFileTime;
                cell.ProcessName = match.ProcessName;
                cell.Title = match.Title;
                cell.Icon = match.Icon;
                cell.ExecutablePath = match.ExecutablePath;
                cell.CommandLineArguments = match.CommandLineArguments;
                placed.Add(match);
            }
            else
            {
                cell.Icon = IconHelper.ForExecutable(pin.ExecutablePath);
            }

            cells[pin.Key] = cell;
        }

        // 2. Remaining windows, priority queue first (Edge, VS Code), z-order as tiebreak.
        var pool = windows
            .Where(w => !placed.Contains(w))
            .OrderBy(w => w.Priority)
            .ThenBy(w => w.ZOrder)
            .ToList();

        // 3. First tier: exact initial-letter matches.
        var secondTier = new List<RunningWindow>();
        foreach (var window in pool)
        {
            if (window.PreferredLetter is char wanted && !cells.ContainsKey(wanted))
            {
                cells[wanted] = HiveCell.FromWindow(wanted, window);
            }
            else
            {
                secondTier.Add(window);
            }
        }

        // 4. Second tier: nearest free cell around the one each window originally wanted.
        foreach (var window in secondTier)
        {
            char target = window.PreferredLetter ?? 'G'; // grid centre when the name has no A-Z initial
            char? free = null;
            foreach (char candidate in KeyGrid.ByDistanceFrom(target))
            {
                if (!cells.ContainsKey(candidate))
                {
                    free = candidate;
                    break;
                }
            }
            if (free == null)
                break; // grid full

            cells[free.Value] = HiveCell.FromWindow(free.Value, window);
        }

        return cells.Values.OrderBy(c => c.Letter).ToList();
    }
}
