using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HiveMotion;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action<TaskGridView> Run)[]
        {
            ("Applying the same cells twice performs no per-cell updates", SameCellsNoRerender),
            ("Only the changed letter re-renders on a partial update", PartialUpdate),
            ("A changed window handle re-renders its cell", HandleChangeRerenders),
            ("Opening from a non-searching state touches no search state", CleanOpenSkipsResets),
            ("The first search entry builds the deferred result rows", FirstSearchBuildsRows)
        };
        try
        {
            RunGateChecks();
            Console.WriteLine("PASS The opening gate retains only the newest snapshot and releases it once");
            foreach (var test in tests)
            {
                var grid = new TaskGridView();
                test.Run(grid);
                Console.WriteLine($"PASS {test.Name}");
            }
            Console.WriteLine($"{tests.Length} opening checks passed. No interactive windows were shown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Logger.Shutdown(); }
    }

    private static void SameCellsNoRerender(TaskGridView grid)
    {
        var cells = new List<HiveCell> { Cell('A', "Alpha"), Cell('B', "Beta") };
        grid.SetCells(cells);
        long applied = Sum(grid, v => v.SetCellCount);
        Assert(applied == 2, $"first apply should update the two occupied cells, got {applied}");

        grid.SetCells(cells);

        Assert(Sum(grid, v => v.SetCellCount) == applied,
            "reapplying identical cells must not re-run per-cell updates");
        Assert(View(grid, 'A').Visibility == Visibility.Visible, "occupied cell A must stay visible");
        Assert(View(grid, 'C').Visibility == Visibility.Collapsed, "unoccupied cell C must stay collapsed");
    }

    private static void PartialUpdate(TaskGridView grid)
    {
        var a = Cell('A', "Alpha");
        var b = Cell('B', "Beta");
        grid.SetCells(new[] { a, b });

        var renamed = Cell('A', "Alpha renamed");
        grid.SetCells(new[] { renamed, b });

        Assert(View(grid, 'A').SetCellCount == 2, "the renamed cell must be updated");
        Assert(View(grid, 'B').SetCellCount == 1, "the unchanged cell must not be updated");
        Assert(View(grid, 'A').Visibility == Visibility.Visible,
            "an updated still-occupied cell must remain visible without a visibility flip");
    }

    private static void HandleChangeRerenders(TaskGridView grid)
    {
        var a = Cell('A', "Alpha", new IntPtr(100));
        grid.SetCells(new[] { a });
        Assert(View(grid, 'A').SetCellCount == 1, "setup: initial apply");

        var moved = Cell('A', "Alpha", new IntPtr(200));
        grid.SetCells(new[] { moved });

        Assert(View(grid, 'A').SetCellCount == 2,
            "a new window handle is identity-relevant and must re-render the cell");
    }

    private static void CleanOpenSkipsResets(TaskGridView grid)
    {
        var cells = new List<HiveCell> { Cell('A', "Alpha") };
        grid.SetCells(cells);
        // Emulate the overlay close: resets search transforms once, then restores every
        // transient property to its clean default.
        grid.ResetForOverlayClose();
        long setCells = Sum(grid, v => v.SetCellCount);
        long resets = Sum(grid, v => v.ResetSearchTransformCount);
        int rebuilds = grid.RebuildResultsCount;

        grid.SetCells(cells); // reopen with unchanged content

        Assert(Sum(grid, v => v.SetCellCount) == setCells,
            "a clean reopen must not re-run per-cell updates");
        Assert(Sum(grid, v => v.ResetSearchTransformCount) == resets,
            "a clean reopen must not touch search transforms");
        Assert(grid.RebuildResultsCount == rebuilds,
            "a clean reopen must not rebuild the search rows on the opening path");
    }

    private static void FirstSearchBuildsRows(TaskGridView grid)
    {
        var cells = new List<HiveCell> { Cell('A', "Alpha"), Cell('B', "Beta") };
        grid.SetCells(cells);
        Assert(grid.RebuildResultsCount == 0, "setup: the deferred build has not run without a frame");

        HiveCell? chosen = null;
        grid.CellChosen += (_, c) => chosen = c;
        grid.EnterSearch(); // first Space press: rows must exist even if ContextIdle never fired
        grid.SubmitSearch(); // Enter on the default highlight

        Assert(grid.RebuildResultsCount == 1, "entering search must build the rows synchronously");
        Assert(chosen != null, "submitting the search must choose the highlighted row");
    }

    private static void RunGateChecks()
    {
        static WindowSnapshot Snapshot(int tick) =>
            new(Array.Empty<RunningWindow>(), new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(tick));

        var gate = new OpeningUpdateGate();
        Assert(!gate.IsOpen, "a new gate starts closed");
        var first = Snapshot(1);
        Assert(!gate.TryRetain(first), "a closed gate passes snapshots straight through");

        gate.Open();
        Assert(gate.IsOpen, "the gate reports open");
        Assert(gate.TryRetain(first), "an open gate retains the first snapshot");
        var newest = Snapshot(2);
        Assert(gate.TryRetain(newest), "an open gate retains the superseding snapshot");
        Assert(ReferenceEquals(gate.Close(), newest),
            "release surfaces only the newest snapshot; the superseded one is never applied");
        Assert(!gate.IsOpen, "release closes the gate");
        Assert(gate.Close() == null, "a second release returns nothing");

        gate.Open();
        Assert(gate.TryRetain(Snapshot(3)), "reopen retains again");
        gate.Open(); // a fast close/reopen discards the previous opening's residue
        Assert(gate.Close() == null, "reopening the gate discards the stale retained snapshot");
    }

    private static HiveCell Cell(char letter, string name, IntPtr handle = default) => new()
    {
        Letter = letter,
        AppName = name,
        Title = name,
        ProcessName = name,
        WindowHandle = handle
    };

    private static HiveCellView View(TaskGridView grid, char letter)
    {
        var canvas = (Canvas)grid.FindName("HexCanvas")!;
        return canvas.Children.OfType<HiveCellView>().First(v => v.PoolLetter == letter);
    }

    private static long Sum(TaskGridView grid, Func<HiveCellView, int> count)
    {
        var canvas = (Canvas)grid.FindName("HexCanvas")!;
        return canvas.Children.OfType<HiveCellView>().Sum(v => (long)count(v));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
