using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreLoader.Analyzers;

/// <summary>
/// Reports a mod assembly that does not say which game it is for (CL0004), the
/// loader refuses such a mod at runtime; and a mod written against one game's
/// generated interop that declares other games, or any game (CL0005).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GameDeclarationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.NoGameDeclared, Rules.InteropGameMismatch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext c)
    {
        var info = c.Compilation.GetTypeByMetadataName("CoreLoader.CoreModInfoAttribute");
        if (info is null) return;   // not compiled against CoreLoader
        var gameAttr = c.Compilation.GetTypeByMetadataName("CoreLoader.CoreModGameAttribute");
        var anyAttr = c.Compilation.GetTypeByMetadataName("CoreLoader.CoreModAnyGameAttribute");

        var attributes = c.Compilation.Assembly.GetAttributes();
        var modInfo = attributes.FirstOrDefault(a => Is(a, info));
        if (modInfo is null) return;   // a library, not a mod

        var game = attributes.FirstOrDefault(a => Is(a, gameAttr));
        bool any = attributes.Any(a => Is(a, anyAttr));
        var location = modInfo.ApplicationSyntaxReference?.GetSyntax(c.CancellationToken).GetLocation() ?? Location.None;

        string[] named = game is null
            ? Array.Empty<string>()
            : game.ConstructorArguments
                .SelectMany(a => a.Kind != TypedConstantKind.Array ? ImmutableArray.Create(a)
                                 : a.IsNull ? ImmutableArray<TypedConstant>.Empty : a.Values)
                .Select(v => v.Value as string)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray();

        // The same three cases the loader refuses (ModManager.CheckGame).
        string? problem = (game, any) switch
        {
            (null, false) => "This mod does not say which game it is for",
            (not null, true) => "This mod carries both [CoreModGame] and [CoreModAnyGame]",
            (not null, false) when named.Length == 0 => "This mod's [CoreModGame] names no game",
            _ => null,
        };
        if (problem != null)
        {
            c.ReportDiagnostic(Diagnostic.Create(Rules.NoGameDeclared, location, problem));
            return;
        }

        // The interop a mod compiles against is "<game>.Interop", named after the
        // game's exe the way InteropGenerator.SafeGameName makes it, and holding
        // a <game>.Scripts class: that tells it from any other "*.Interop".
        var interop = c.Compilation.ReferencedAssemblyNames
            .Select(n => n.Name)
            .Where(n => n.EndsWith(".Interop", StringComparison.OrdinalIgnoreCase) && n.Length > ".Interop".Length)
            .FirstOrDefault(n => c.Compilation.GetTypeByMetadataName(n.Substring(0, n.Length - ".Interop".Length) + ".Scripts") != null);
        if (interop is null) return;
        string interopGame = interop.Substring(0, interop.Length - ".Interop".Length);
        if (named.Any(n => string.Equals(SafeName(n), interopGame.TrimStart('_'), StringComparison.OrdinalIgnoreCase)))
            return;

        var where = (any ? null : game?.ApplicationSyntaxReference)?.GetSyntax(c.CancellationToken).GetLocation()
                    ?? attributes.FirstOrDefault(a => Is(a, anyAttr))?.ApplicationSyntaxReference?.GetSyntax(c.CancellationToken).GetLocation()
                    ?? location;
        c.ReportDiagnostic(Diagnostic.Create(Rules.InteropGameMismatch, where, interop,
            any ? "[CoreModAnyGame]" : $"[CoreModGame({string.Join(", ", named.Select(n => "\"" + n + "\""))})]"));
    }

    private static bool Is(AttributeData a, INamedTypeSymbol? type) =>
        type is not null && SymbolEqualityComparer.Default.Equals(a.AttributeClass, type);

    // As InteropGenerator.SafeGameName: every character that is not a letter or
    // digit becomes '_', and the ends are trimmed of them.
    private static string SafeName(string exe) =>
        new string(exe.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
}
