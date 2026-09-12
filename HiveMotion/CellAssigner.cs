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
                case WindowViewMotion windowView:
                    cells[windowView.Key] = WindowViewCell(windowView);
                    break;
            }
        }

        AssignWindows(windows, cells, placed);
        return cells.Values.OrderBy(c => c.Letter).ToList();
    }

    /// <summary>
    /// Dynamic window-view layer: filter the latest snapshot, then apply the same
    /// priority, preferred-letter, and nearest-free-cell rules without reservations.
    /// </summary>
    public IReadOnlyList<HiveCell> AssignWindowView(WindowViewMotion view, IReadOnlyList<RunningWindow> windows)
    {
        var cells = new Dictionary<char, HiveCell>();
        AssignWindows(windows.Where(view.Matches), cells, new HashSet<RunningWindow>());
        return cells.Values.OrderBy(c => c.Letter).ToList();
    }

    private static void AssignWindows(IEnumerable<RunningWindow> windows, Dictionary<char, HiveCell> cells,
        HashSet<RunningWindow> placed)
    {
        // Remaining windows, priority queue first (Edge, VS Code), z-order as tiebreak.
        var pool = windows
            .Where(w => !placed.Contains(w))
            .OrderBy(w => w.Priority)
            .ThenBy(w => w.ZOrder)
            .ToList();

        // First tier: exact initial-letter matches.
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

        // Second tier: nearest free cell around the one each window originally wanted.
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
        string displayName = app.DisplayName.Length > 0
            ? app.DisplayName
            : Loc.Get("Motion_ApplicationName");
        var cell = new HiveCell
        {
            Letter = app.Key,
            Motion = app,
            IconRequest = IconRequest.ForMotion(app),
            AppName = displayName,
            Title = displayName
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

        return cell;
    }

    private static HiveCell FolderCell(FolderMotion folder) => new()
    {
        Letter = folder.Key,
        Motion = folder,
        AppName = folder.DisplayName,
        Title = folder.DisplayName,
        IconRequest = IconRequest.ForMotion(folder)
    };

    private static HiveCell WindowViewCell(WindowViewMotion view) => new()
    {
        Letter = view.Key,
        Motion = view,
        AppName = view.DisplayName,
        Title = view.DisplayName,
        IconRequest = IconRequest.ForMotion(view)
    };

    /// <summary>System actions never bind a window: name and glyph icon come from the catalog.</summary>
    private static HiveCell SystemActionCell(SystemActionMotion motion)
    {
        string displayName = motion.IsConfigured
            ? SystemActions.DisplayNameOf(motion.ActionId)
            : Loc.Get("Motion_SystemActionName");
        return new HiveCell
        {
            Letter = motion.Key,
            Motion = motion,
            AppName = displayName,
            Title = displayName,
            IconRequest = IconRequest.ForMotion(motion)
        };
    }
}
