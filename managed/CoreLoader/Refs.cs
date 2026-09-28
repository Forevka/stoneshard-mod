namespace CoreLoader;

/// <summary>
/// A compiled GML script, by name. The generated <c>&lt;Game&gt;.Interop</c>
/// assembly exposes one of these per script, so calls and hooks are checked by
/// the compiler instead of being magic strings. Resolution is lazy: a script
/// removed by a game update fails when it is used, with its name in the error.
/// </summary>
public class ScriptRef
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

// Typed variants for scripts whose argument count was read from their code:
// the generated interop uses these, so a call with the wrong number of
// arguments is a compile error rather than a surprise at runtime.

/// <summary>A script that reads 1 argument.</summary>
public sealed class ScriptRef1 : ScriptRef
{
    public ScriptRef1(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0) => Call(a0);
}

/// <summary>A script that reads 2 arguments.</summary>
public sealed class ScriptRef2 : ScriptRef
{
    public ScriptRef2(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1) => Call(a0, a1);
}

/// <summary>A script that reads 3 arguments.</summary>
public sealed class ScriptRef3 : ScriptRef
{
    public ScriptRef3(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2) => Call(a0, a1, a2);
}

/// <summary>A script that reads 4 arguments.</summary>
public sealed class ScriptRef4 : ScriptRef
{
    public ScriptRef4(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2, RValue a3) => Call(a0, a1, a2, a3);
}

/// <summary>A script that reads 5 arguments.</summary>
public sealed class ScriptRef5 : ScriptRef
{
    public ScriptRef5(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2, RValue a3, RValue a4) => Call(a0, a1, a2, a3, a4);
}

/// <summary>A script that reads 6 arguments.</summary>
public sealed class ScriptRef6 : ScriptRef
{
    public ScriptRef6(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2, RValue a3, RValue a4, RValue a5) =>
        Call(a0, a1, a2, a3, a4, a5);
}

/// <summary>A script that reads 7 arguments.</summary>
public sealed class ScriptRef7 : ScriptRef
{
    public ScriptRef7(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2, RValue a3, RValue a4, RValue a5, RValue a6) =>
        Call(a0, a1, a2, a3, a4, a5, a6);
}

/// <summary>A script that reads 8 arguments.</summary>
public sealed class ScriptRef8 : ScriptRef
{
    public ScriptRef8(string symbol) : base(symbol) { }
    public RValue Invoke(RValue a0, RValue a1, RValue a2, RValue a3, RValue a4, RValue a5, RValue a6, RValue a7) =>
        Call(a0, a1, a2, a3, a4, a5, a6, a7);
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
