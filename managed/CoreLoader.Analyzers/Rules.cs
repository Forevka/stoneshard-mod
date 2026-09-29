using Microsoft.CodeAnalysis;

namespace CoreLoader.Analyzers;

/// <summary>The diagnostics CoreLoader reports in mods.</summary>
public static class Rules
{
    private const string Category = "CoreLoader.Lifetime";
    private const string HelpLink = "https://github.com/Forevka/stoneshard-mod/blob/main/managed/README.md#analyzers";

    public static readonly DiagnosticDescriptor StoredRValue = new(
        id: "CL0001",
        title: "A game value is kept in a field",
        messageFormat: "'{0}' stores a CoreLoader.RValue across frames: strings, arrays and structs from the game are pooled and released at the end of the frame, so this can dangle. Keep C# data instead (AsString, AsReal), or take ownership with Values.Keep and release it with Values.Free.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A GameMaker string, array or struct handed to a mod is only valid for the current frame. Numbers are safe to hold, but the type cannot tell; suppress with a justification when the value is kept with Values.Keep or is only ever a number.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor StoredTransient = new(
        id: "CL0002",
        title: "A per-call game handle outlives its call",
        messageFormat: "'{0}' keeps a {1} beyond the call that produced it: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Instance is a raw CInstance pointer, which dangles once the instance is destroyed; hold an InstanceRef (by id) instead. HookCall points into the hooked call's frame and is valid only inside the handler.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor FreedLentValue = new(
        id: "CL0003",
        title: "Freeing a value the game lent",
        messageFormat: "'{0}' holds {1}, which the game lends to the handler: never free it. Free only what Values.Keep or Values.Copy returned.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A hook's arguments and result belong to the caller. Releasing one frees the caller's reference, which then crashes or corrupts the game when it is used again.",
        helpLinkUri: HelpLink);

    internal const string InstanceWhy = "it is a raw CInstance pointer that dangles once the instance is destroyed; hold an InstanceRef (by id) instead";
    internal const string HookCallWhy = "a HookCall is valid only inside its handler; copy what you need (GetArg(i).AsReal, Symbol) instead";
}
