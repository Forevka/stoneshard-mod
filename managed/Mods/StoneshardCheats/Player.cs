using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// The player instance, as a CInstance pointer that scripts can run as.
/// </summary>
/// <remarks>
/// GML hands out the player only as an id (instance_find), and scripts need the
/// instance itself as their self, so the pointer is taken from o_player's own
/// Step event. It is trusted only while that event keeps running: after
/// quitting to the menu, or across a save load, the instance can be destroyed
/// and its memory reused, and a remembered pointer would then run character
/// scripts as some other object.
/// </remarks>
internal static class Player
{
    // Half a second without a step: the game is paused, in a menu, or the
    // player is gone. Every use re-checks, nothing caches the Instance.
    private const long StaleAfterMs = 500;

    private static Instance _self;
    private static RValue _id = RValue.Undefined;
    private static long _seenAt;

    internal static void OnStep(HookCall c)
    {
        var self = c.Self;
        if (self.IsNull) return;
        if (!self.Equals(_self))
        {
            _self = self;
            _id = self.Get("id");
            // A number keeps nothing alive; a reference is a plain handle too.
            // Neither is a pooled string or array, so it may outlive the frame.
        }
        _seenAt = Environment.TickCount64;
    }

    /// <summary>The live player, or null while it is not stepping.</summary>
    public static Instance? Current
    {
        get
        {
            if (_self.IsNull || Environment.TickCount64 - _seenAt > StaleAfterMs) return null;
            if (!Game.CallBuiltin("instance_exists", _id).AsBool) return null;
            return _self;
        }
    }

    public static bool Available => Current != null;

    /// <summary>The live player; throws when there is none.</summary>
    public static Instance Require() =>
        Current ?? throw new InvalidOperationException("no player: load a save and let the game run");

    /// <summary>Runs a script as the player (self and other).</summary>
    public static RValue Call(string script, params RValue[] args)
    {
        var p = Require();
        return Game.CallScriptAs(p, p, script, args);
    }

    /// <summary>Calls a builtin with the player as self.</summary>
    public static RValue Builtin(string name, params RValue[] args) =>
        Game.CallBuiltinAs(Require(), name, args);

    public static (double X, double Y) Position
    {
        get
        {
            var p = Require();
            return (p.Get("x").AsReal, p.Get("y").AsReal);
        }
    }
}
