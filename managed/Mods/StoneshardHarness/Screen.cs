using System.Diagnostics;
using System.Runtime.InteropServices;
using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// Room and GUI coordinates to desktop pixels and back.
/// </summary>
/// <remarks>
/// Found on the running game (0.9.4.25, 1920x1080):
///   * the world is drawn through view 0's camera (view_get_camera(0)): a
///     960x540 view, scaled onto the application surface, which
///     application_get_position() places in the window;
///   * the GUI is not the Draw GUI layer: Stoneshard's windows are instances
///     in room space under a camera of their own, global.cameraGUI, whose view
///     sits at (-5000, -5000) and is 960x540 too, drawn over the whole window;
///   * a floor click at the pixel this maps a cell centre to walks there (it
///     runs scr_player_move with that cell's room coordinates), which is how
///     the mapping was checked.
/// Desktop pixels are the window's client pixels plus where the client area
/// sits on the desktop, so they can be handed to a mouse tool as they are.
/// </remarks>
internal static partial class Screen
{
    public readonly record struct View(double X, double Y, double W, double H);

    public static View WorldView()
    {
        var cam = Builtins.view_get_camera(0);
        return new View(Builtins.camera_get_view_x(cam).AsReal, Builtins.camera_get_view_y(cam).AsReal,
                        Builtins.camera_get_view_width(cam).AsReal, Builtins.camera_get_view_height(cam).AsReal);
    }

    public static View? GuiView()
    {
        var cam = Globals.Get("cameraGUI");
        if (!cam.IsNumber || cam.AsReal < 0) return null;
        return new View(Builtins.camera_get_view_x(cam).AsReal, Builtins.camera_get_view_y(cam).AsReal,
                        Builtins.camera_get_view_width(cam).AsReal, Builtins.camera_get_view_height(cam).AsReal);
    }

    /// <summary>The application surface's rectangle in client pixels (x1, y1, x2, y2).</summary>
    public static (double X1, double Y1, double X2, double Y2) AppRect()
    {
        var a = Builtins.application_get_position();
        double x2 = Gm.ArrNum(a, 2), y2 = Gm.ArrNum(a, 3);
        if (x2 <= 0 || y2 <= 0) return (0, 0, Builtins.window_get_width().AsReal, Builtins.window_get_height().AsReal);
        return (Gm.ArrNum(a, 0), Gm.ArrNum(a, 1), x2, y2);
    }

    /// <summary>Where the window's client area starts on the desktop.</summary>
    public static (int X, int Y) ClientOrigin()
    {
        var h = Window;
        if (h == 0) return (0, 0);
        var p = new Point();
        return ClientToScreen(h, ref p) ? (p.X, p.Y) : (0, 0);
    }

    /// <summary>A room position as desktop pixels.</summary>
    public static (int X, int Y) FromRoom(double x, double y)
    {
        var v = WorldView();
        var r = AppRect();
        var o = ClientOrigin();
        return ((int)Math.Round(o.X + r.X1 + (x - v.X) * (r.X2 - r.X1) / v.W),
                (int)Math.Round(o.Y + r.Y1 + (y - v.Y) * (r.Y2 - r.Y1) / v.H));
    }

    /// <summary>Desktop pixels as a room position.</summary>
    public static (double X, double Y) ToRoom(int sx, int sy)
    {
        var v = WorldView();
        var r = AppRect();
        var o = ClientOrigin();
        return (v.X + (sx - o.X - r.X1) * v.W / (r.X2 - r.X1), v.Y + (sy - o.Y - r.Y1) * v.H / (r.Y2 - r.Y1));
    }

    /// <summary>A GUI-camera position (a window or button instance's x/y) as desktop pixels.</summary>
    public static (int X, int Y)? FromGui(double x, double y)
    {
        if (GuiView() is not { } v) return null;
        var o = ClientOrigin();
        double w = Builtins.window_get_width().AsReal, h = Builtins.window_get_height().AsReal;
        return ((int)Math.Round(o.X + (x - v.X) * w / v.W), (int)Math.Round(o.Y + (y - v.Y) * h / v.H));
    }

    /// <summary>Whether a desktop point falls inside the window's client area.</summary>
    public static bool OnScreen((int X, int Y) p)
    {
        var o = ClientOrigin();
        double w = Builtins.window_get_width().AsReal, h = Builtins.window_get_height().AsReal;
        return p.X >= o.X && p.Y >= o.Y && p.X < o.X + w && p.Y < o.Y + h;
    }

    // ------------------------------------------------------------ window

    private static nint _window;

    /// <summary>The game's main window (the process's top-level window).</summary>
    public static nint Window
    {
        get
        {
            if (_window != 0 && IsWindow(_window)) return _window;
            using var self = Process.GetCurrentProcess();
            _window = self.MainWindowHandle;
            return _window;
        }
    }

    public static bool IsForeground => Window != 0 && GetForegroundWindow() == Window;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);
}
