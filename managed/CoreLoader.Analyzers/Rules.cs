using Microsoft.CodeAnalysis;

namespace CoreLoader.Analyzers;

/// <summary>The diagnostics CoreLoader reports in mods.</summary>
public static class Rules
{
    private const string Category = "CoreLoader.Lifetime";
    // Each rule links to its own section of the documentation site's analyzer page.
    private const string HelpLink = "https://forevka.github.io/stoneshard-mod/modding/reference/analyzers#";

    public static readonly DiagnosticDescriptor StoredRValue = new(
        id: "CL0001",
        title: "A game value is kept in a field",
        messageFormat: "'{0}' stores a CoreLoader.RValue across frames: strings, arrays and structs from the game are pooled and released at the end of the frame, so this can dangle. Keep C# data instead (ToString(), AsReal), or take ownership with Values.Keep and release it with Values.Free.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A GameMaker string, array or struct handed to a mod is only valid for the current frame. Numbers are safe to hold, but the type cannot tell; suppress with a justification when the value is kept with Values.Keep or is only ever a number.",
        helpLinkUri: HelpLink + "cl0001");

    public static readonly DiagnosticDescriptor StoredTransient = new(
        id: "CL0002",
        title: "A per-call game handle outlives its call",
        messageFormat: "'{0}' keeps a {1} beyond the call that produced it: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Instance is a raw CInstance pointer, which dangles once the instance is destroyed; hold an InstanceRef (by id) instead. HookCall points into the hooked call's frame and is valid only inside the handler.",
        helpLinkUri: HelpLink + "cl0002");

    public static readonly DiagnosticDescriptor FreedLentValue = new(
        id: "CL0003",
        title: "Freeing a value the game lent",
        messageFormat: "'{0}' holds {1}, which the game lends to the handler: never free it. Free only what Values.Keep or Values.Copy returned.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A hook's arguments and result belong to the caller. Releasing one frees the caller's reference, which then crashes or corrupts the game when it is used again.",
        helpLinkUri: HelpLink + "cl0003");

    public static readonly DiagnosticDescriptor NoGameDeclared = new(
        id: "CL0004",
        title: "A mod does not say which game it is for",
        messageFormat: "{0}, and the loader refuses to load it. Declare exactly one of [assembly: CoreModGame(\"<exe name>\")] for a mod written for particular games, or [assembly: CoreModAnyGame] for one that works in any game.",
        category: GameCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every mod declares its games: code written for one game's objects and scripts must not run inside another.",
        helpLinkUri: HelpLink + "cl0004",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InteropGameMismatch = new(
        id: "CL0005",
        title: "A mod built on one game's interop declares other games",
        messageFormat: "This mod compiles against {0} but declares {1}: it can only work in the game that interop was generated from. Name that game in [CoreModGame].",
        category: GameCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A generated interop names one game's scripts, objects and assets; a mod written against it fails in any other game.",
        helpLinkUri: HelpLink + "cl0005",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private const string GameCategory = "CoreLoader.Games";

    internal const string InstanceWhy = "it is a raw CInstance pointer that dangles once the instance is destroyed; hold an InstanceRef (by id) instead";
    internal const string HookCallWhy = "a HookCall is valid only inside its handler; copy what you need (GetArg(i).AsReal, Symbol) instead";
}
