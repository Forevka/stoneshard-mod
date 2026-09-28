namespace CoreLoader;

/// <summary>
/// A compiled GML script, by name. The generated <c>&lt;Game&gt;.Interop</c>
/// assembly exposes one of these per script, so calls and hooks are checked by
/// the compiler instead of being magic strings. Resolution is lazy: a script
/// removed by a game update fails when it is used, with its name in the error.
/// </summary>
public sealed class ScriptRef
{
    private nint _address;

    public ScriptRef(string symbol) => Symbol = symbol;

    /// <summary>Full symbol, e.g. "gml_Script_scr_loot".</summary>
    public string Symbol { get; }

    /// <summary>Whether the running game has this script.</summary>
    public bool Exists => Address != 0;

    public nint Address => _address != 0 ? _address : _address = Game.FindSymbol(Symbol);

    public RValue Call(params RValue[] args) => Game.CallScript(Symbol, args);

    public RValue CallAs(Instance self, Instance other, params RValue[] args) =>
        Game.CallScriptAs(self, other, Symbol, args);

    public HookHandle Before(HookHandler handler) => Hooks.Before(Symbol, handler);

    public HookHandle After(HookHandler handler) => Hooks.After(Symbol, handler);

    public override string ToString() => Symbol;
}

/// <summary>An object event (Create, Step, Draw, Alarm, ...), by name.</summary>
public sealed class EventRef
{
    private nint _address;

    public EventRef(string symbol) => Symbol = symbol;

    /// <summary>Full symbol, e.g. "gml_Object_o_player_Step_0".</summary>
    public string Symbol { get; }

    public bool Exists => Address != 0;

    public nint Address => _address != 0 ? _address : _address = Game.FindSymbol(Symbol);

    /// <summary>Runs the event as <paramref name="self"/>.</summary>
    public void Call(Instance self, Instance other = default) => Game.CallEvent(Symbol, self, other);

    public HookHandle Before(HookHandler handler) => Hooks.Before(Symbol, handler);

    public HookHandle After(HookHandler handler) => Hooks.After(Symbol, handler);

    public override string ToString() => Symbol;
}
