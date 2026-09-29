namespace CoreLoader;

/// <summary>A click taken in pick mode.</summary>
/// <param name="X">Client pixels from the window's left edge.</param>
/// <param name="Y">Client pixels from the window's top edge.</param>
/// <param name="Width">Client width when the click happened.</param>
/// <param name="Height">Client height when the click happened.</param>
/// <param name="RightButton">A right click - by convention, "cancel".</param>
/// <param name="RoomX">Where the game's own mouse was in the room (device_mouse_x(0)), read as the click was taken.</param>
/// <param name="RoomY">As <paramref name="RoomX"/>, vertically.</param>
public readonly record struct PickClick(int X, int Y, int Width, int Height, bool RightButton, double RoomX, double RoomY);

/// <summary>
/// Pick mode: let the user click on something in the game without the game
/// reacting to the click. Game thread only.
/// </summary>
public static unsafe class Input
{
    private static bool _armed;

    /// <summary>
    /// Arms pick mode: the next left or right click outside the overlay's
    /// windows is swallowed and reported by <see cref="TryTakePick"/>.
    /// </summary>
    public static void ArmPick()
    {
        Loader.EnsureGameThread();
        _armed = true;
        Loader.Api->InputPickArm(1);
    }

    /// <summary>Disarms pick mode and forgets a click nobody took.</summary>
    public static void CancelPick()
    {
        Loader.EnsureGameThread();
        _armed = false;
        Loader.Api->InputPickArm(0);
    }

    /// <summary>Whether pick mode is waiting for a click.</summary>
    public static bool IsPicking => _armed;

    /// <summary>The click pick mode took, once; pick mode is disarmed by it.</summary>
    public static bool TryTakePick(out PickClick click)
    {
        Loader.EnsureGameThread();
        int x, y, w, h, b;
        if (Loader.Api->InputPickTake(&x, &y, &w, &h, &b) == 0)
        {
            click = default;
            return false;
        }
        _armed = false;
        // The game still sees mouse movement (only the button is swallowed),
        // so its own idea of the mouse - views, scaling, letterboxing and all -
        // is where the click was.
        double rx = double.NaN, ry = double.NaN;
        try
        {
            rx = Game.CallBuiltin("device_mouse_x", 0).AsReal;
            ry = Game.CallBuiltin("device_mouse_y", 0).AsReal;
        }
        catch (GmlException) { }
        click = new PickClick(x, y, w, h, b == 1, rx, ry);
        return true;
    }
}
