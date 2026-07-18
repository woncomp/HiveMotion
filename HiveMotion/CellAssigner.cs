using System;
using System.Collections.Generic;
using System.Linq;

namespace HiveMotion;

/// <summary>
/// Places running windows onto the keyboard hex grid.
/// Decision chain: preset actions pin their letter first; then windows in user-priority
/// order grab the cell matching their app's initial; losers take the nearest free cell
/// around the one they wanted (second tier), after all exact matches are done.
/// </summary>
public sealed class CellAssigner
{
    private readonly AppConfig _config;

    public CellAssigner(AppConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<HiveCell> Assign(IReadOnlyList<RunningWindow> windows)
    {
        var cells = new Dictionary<char, HiveCell>();
        var placed = new HashSet<RunningWindow>();

        // 1. Preset actions occupy their configured letter first.
        foreach (var preset in _config.Presets)
        {
            var cell = new HiveCell
            {
                Letter = preset.Key,
                IsPreset = true,
                Preset = preset,
                AppName = preset.DisplayName,
                Title = preset.DisplayName
            };

            var match = windows.FirstOrDefault(w =>
                w.ProcessName.Equals(preset.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                cell.WindowHandle = match.Handle;
                cell.Title = match.Title;
                cell.Icon = match.Icon;
                placed.Add(match);
            }
            else
            {
                cell.Icon = IconHelper.ForExecutable(preset.ExecutablePath);
            }

            cells[preset.Key] = cell;
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
