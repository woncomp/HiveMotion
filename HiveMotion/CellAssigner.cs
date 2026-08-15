using System;
using System.Collections.Generic;
using System.Linq;

namespace HiveMotion;

/// <summary>
/// Places running windows and motions onto the keyboard hex grid.
/// Home-layer decision chain: motions reserve their letter first (applications only
/// accept windows of the same program with the same arguments; folders never match
/// windows); then windows in user-priority order grab the cell matching their app's
/// initial; losers take the nearest free cell around the one they wanted (second
/// tier), after all exact matches are done.
/// </summary>
public sealed class CellAssigner
{
    private readonly IReadOnlyList<Motion> _homeMotions;

    public CellAssigner(IReadOnlyList<Motion> homeMotions)
    {
        _homeMotions = homeMotions;
    }

    public IReadOnlyList<HiveCell> Assign(IReadOnlyList<RunningWindow> windows)
    {
        var cells = new Dictionary<char, HiveCell>();
        var placed = new HashSet<RunningWindow>();

        // 1. Motions reserve their configured letter first.
        foreach (var motion in _homeMotions)
        {
            switch (motion)
            {
                case ApplicationMotion app:
                    cells[app.Key] = AssignApplication(app, windows, placed);
                    break;
                case FolderMotion folder:
                    cells[folder.Key] = FolderCell(folder);
                    break;
                case SystemActionMotion systemAction:
                    cells[systemAction.Key] = SystemActionCell(systemAction);
                    break;
            }
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

    /// <summary>
    /// Folder layer: only the folder's own items occupy cells. Unlike the home layer,
    /// empty letters stay empty — scanned windows are never backfilled into a folder.
    /// </summary>
    public IReadOnlyList<HiveCell> AssignFolder(FolderMotion folder, IReadOnlyList<RunningWindow> windows)
    {
        var cells = new List<HiveCell>();
        var placed = new HashSet<RunningWindow>();

        foreach (var item in folder.Items)
        {
            switch (item)
            {
                case ApplicationMotion app:
                    cells.Add(AssignApplication(app, windows, placed));
                    break;
                case SystemActionMotion systemAction:
                    cells.Add(SystemActionCell(systemAction));
                    break;
                // Nesting is rejected at store load and in the manage center; ignore defensively.
            }
        }

        return cells.OrderBy(c => c.Letter).ToList();
    }

    private static HiveCell AssignApplication(ApplicationMotion app, IReadOnlyList<RunningWindow> windows,
        HashSet<RunningWindow> placed)
    {
        var cell = new HiveCell
        {
            Letter = app.Key,
            Motion = app,
            AppName = app.DisplayName,
            Title = app.DisplayName
        };

        // Scan order is z-order, so the first match is the topmost matching window.
        var match = windows.FirstOrDefault(w => !placed.Contains(w) && app.Matches(w));
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
            cell.Icon = IconHelper.ForExecutable(app.ExecutablePath);
        }

        return cell;
    }

    private static HiveCell FolderCell(FolderMotion folder) => new()
    {
        Letter = folder.Key,
        Motion = folder,
        AppName = folder.DisplayName,
        Title = folder.DisplayName,
        Icon = folder.IconPath.Length > 0 ? IconHelper.ForImageFile(folder.IconPath) : null
    };

    /// <summary>System actions never bind a window: name and glyph icon come from the catalog.</summary>
    private static HiveCell SystemActionCell(SystemActionMotion motion) => new()
    {
        Letter = motion.Key,
        Motion = motion,
        AppName = SystemActions.DisplayNameOf(motion.ActionId),
        Title = SystemActions.DisplayNameOf(motion.ActionId),
        Icon = GlyphIcon.ForAction(motion.ActionId)
    };
}
