using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreLoader.Analyzers;

/// <summary>
/// Reports game values and per-call handles that a mod keeps past their
/// lifetime (CL0001, CL0002), and values a hook lends that a mod frees (CL0003).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LifetimeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.StoredRValue, Rules.StoredTransient, Rules.FreedLentValue);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = CoreTypes.From(start.Compilation);
            // Not compiled against CoreLoader: nothing to check.
            if (types is null) return;
            start.RegisterSymbolAction(c => AnalyzeMember(c, types), SymbolKind.Field, SymbolKind.Property);
            start.RegisterSyntaxNodeAction(c => AnalyzeLambda(c, types),
                SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeFree(c, types), SyntaxKind.InvocationExpression);
        });
    }

    // CL0001 / CL0002: a field, or an auto-property (which is a field), whose
    // type holds a game value or handle. Computed properties store nothing.
    private static void AnalyzeMember(SymbolAnalysisContext c, CoreTypes types)
    {
        ITypeSymbol type;
        switch (c.Symbol)
        {
            case IFieldSymbol f when !f.IsImplicitlyDeclared && !f.IsConst:
                type = f.Type;
                break;
            case IPropertySymbol p when IsAutoProperty(p):
                type = p.Type;
                break;
            default:
                return;
        }

        var location = c.Symbol.Locations.FirstOrDefault();
        if (location is null) return;
        if (Contains(type, types.RValue))
            c.ReportDiagnostic(Diagnostic.Create(Rules.StoredRValue, location, c.Symbol.Name));
        else if (Contains(type, types.Instance))
            c.ReportDiagnostic(Diagnostic.Create(Rules.StoredTransient, location, c.Symbol.Name, "CoreLoader.Instance", Rules.InstanceWhy));
        else if (Contains(type, types.HookCall))
            c.ReportDiagnostic(Diagnostic.Create(Rules.StoredTransient, location, c.Symbol.Name, "CoreLoader.HookCall", Rules.HookCallWhy));
    }

    private static bool IsAutoProperty(IPropertySymbol p)
    {
        if (p.IsAbstract || p.IsIndexer) return false;
        foreach (var r in p.DeclaringSyntaxReferences)
        {
            switch (r.GetSyntax())
            {
                // A positional record parameter declares an auto-property.
                case ParameterSyntax:
                    return true;
                case PropertyDeclarationSyntax d when d.ExpressionBody is null && d.AccessorList is not null:
                    return d.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null);
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> is <paramref name="target"/>, or a
    /// nullable, array, pointer, tuple or generic collection of it.
    /// </summary>
    private static bool Contains(ITypeSymbol type, INamedTypeSymbol target)
    {
        switch (type)
        {
            case IArrayTypeSymbol a:
                return Contains(a.ElementType, target);
            case IPointerTypeSymbol p:
                return Contains(p.PointedAtType, target);
            case INamedTypeSymbol n:
                if (SymbolEqualityComparer.Default.Equals(n.OriginalDefinition, target)) return true;
                return n.TypeArguments.Any(t => Contains(t, target));
            default:
                return false;
        }
    }

    // A lambda kept for later - stored in a field or collection, queued, or
    // registered as a callback - that captures a frame-bound value: an RValue
    // (CL0001), or an Instance or HookCall (CL0002). The capture lives as long
    // as the lambda, exactly as a field would.
    private static void AnalyzeLambda(SyntaxNodeAnalysisContext c, CoreTypes types)
    {
        var lambda = (AnonymousFunctionExpressionSyntax)c.Node;
        if (!IsStored(lambda, c.SemanticModel, types, c.CancellationToken)) return;

        var flow = c.SemanticModel.AnalyzeDataFlow(lambda);
        if (flow is null || !flow.Succeeded) return;
        foreach (var captured in flow.CapturedInside)
        {
            var type = captured switch
            {
                ILocalSymbol l => l.Type,
                IParameterSymbol p => p.Type,
                _ => null,
            };
            // Only variables from outside: the lambda's own parameters are its to use.
            if (type is null || IsDeclaredInside(captured, lambda)) continue;
            const string kept = "the lambda is kept after the code that made it returns, and ";
            if (Contains(type, types.RValue))
                c.ReportDiagnostic(Diagnostic.Create(Rules.StoredRValue, lambda.GetLocation(), captured.Name));
            else if (Contains(type, types.Instance))
                c.ReportDiagnostic(Diagnostic.Create(Rules.StoredTransient, lambda.GetLocation(),
                    captured.Name, "CoreLoader.Instance", kept + Rules.InstanceWhy));
            else if (Contains(type, types.HookCall))
                c.ReportDiagnostic(Diagnostic.Create(Rules.StoredTransient, lambda.GetLocation(),
                    captured.Name, "CoreLoader.HookCall", kept + Rules.HookCallWhy));
        }
    }

    // CoreLoader methods that keep the delegate they are given and call it on
    // later frames: hook handlers, one-shot hooks, test commands, GUI drawers.
    private static bool IsCallbackRegistration(IMethodSymbol m, CoreTypes types)
    {
        var owner = m.ContainingType;
        if (owner is null) return false;
        if (SymbolEqualityComparer.Default.Equals(owner, types.Hooks))
            return m.Name is "Before" or "After" or "NextBefore" or "NextAfter";
        if (SymbolEqualityComparer.Default.Equals(owner, types.TestHost)) return m.Name == "Register";
        if (SymbolEqualityComparer.Default.Equals(owner, types.GameDraw)) return m.Name == "OnGui";
        return false;
    }

    private static bool IsDeclaredInside(ISymbol symbol, SyntaxNode node) =>
        symbol.DeclaringSyntaxReferences.Any(r => node.Span.Contains(r.Span) && r.SyntaxTree == node.SyntaxTree);

    private static bool IsStored(SyntaxNode lambda, SemanticModel model, CoreTypes types, System.Threading.CancellationToken ct)
    {
        SyntaxNode node = lambda;
        // new Action(() => ...) and (Action)(() => ...) keep the lambda as it is.
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax
               || (node.Parent is ArgumentSyntax && node.Parent.Parent?.Parent is ObjectCreationExpressionSyntax oc
                   && model.GetTypeInfo(oc, ct).Type?.TypeKind == TypeKind.Delegate))
        {
            node = node.Parent is ArgumentSyntax ? node.Parent.Parent!.Parent! : node.Parent!;
        }

        switch (node.Parent)
        {
            // _later = () => ...; OnSomething += () => ...;
            case AssignmentExpressionSyntax a when a.Right == node:
                return model.GetSymbolInfo(a.Left, ct).Symbol is IFieldSymbol or IPropertySymbol or IEventSymbol;
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } }:
                if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol m) return false;
                // Queued or deferred: runs after the handler has returned.
                if (SymbolEqualityComparer.Default.Equals(m.ContainingType, types.Game) && m.Name == "RunOnGameThread") return true;
                if (IsCallbackRegistration(m, types)) return true;
                var ns = m.ContainingType?.ContainingNamespace?.ToDisplayString();
                if (ns == "System.Threading.Tasks" && m.Name is "Run" or "StartNew" or "ContinueWith") return true;
                if (ns == "System.Threading" && m.Name == "QueueUserWorkItem") return true;
                // _pending.Add(() => ...): kept in a collection that outlives the call.
                if (m.Name is "Add" or "Enqueue" or "Push" or "Insert"
                    && call.Expression is MemberAccessExpressionSyntax ma
                    && model.GetSymbolInfo(ma.Expression, ct).Symbol is IFieldSymbol or IPropertySymbol)
                    return true;
                return false;
            default:
                return false;
        }
    }

    // CL0003: Values.Free(ref x) where x was read from a HookCall's GetArg or Result.
    private static void AnalyzeFree(SyntaxNodeAnalysisContext c, CoreTypes types)
    {
        var call = (InvocationExpressionSyntax)c.Node;
        if (call.ArgumentList.Arguments.Count != 1) return;
        // Cheap syntactic filter before asking the semantic model.
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => null,
        };
        if (name != "Free") return;
        if (c.SemanticModel.GetSymbolInfo(call, c.CancellationToken).Symbol is not IMethodSymbol method
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, types.Values)) return;

        var arg = call.ArgumentList.Arguments[0].Expression;
        if (c.SemanticModel.GetSymbolInfo(arg, c.CancellationToken).Symbol is not ILocalSymbol local) return;

        // Every value the local is given in its method: a declaration
        // initializer or an assignment. One from a hook call is enough to flag.
        var body = call.FirstAncestorOrSelf<SyntaxNode>(n =>
            n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        if (body is null) return;

        foreach (var node in body.DescendantNodes())
        {
            ExpressionSyntax? value = node switch
            {
                VariableDeclaratorSyntax d when d.Initializer is not null
                    && SymbolEqualityComparer.Default.Equals(c.SemanticModel.GetDeclaredSymbol(d, c.CancellationToken), local) => d.Initializer.Value,
                AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && SymbolEqualityComparer.Default.Equals(c.SemanticModel.GetSymbolInfo(a.Left, c.CancellationToken).Symbol, local) => a.Right,
                _ => null,
            };
            var lent = value is null ? null : LentFrom(value, c.SemanticModel, types, c.CancellationToken);
            if (lent is null) continue;
            c.ReportDiagnostic(Diagnostic.Create(Rules.FreedLentValue, arg.GetLocation(), local.Name, lent));
            return;
        }
    }

    /// <summary>"a hook argument" / "a hook result" when the expression reads one directly.</summary>
    private static string? LentFrom(ExpressionSyntax value, SemanticModel model, CoreTypes types, System.Threading.CancellationToken ct)
    {
        while (value is ParenthesizedExpressionSyntax p) value = p.Expression;
        var symbol = model.GetSymbolInfo(value, ct).Symbol;
        if (symbol is null || !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, types.HookCall)) return null;
        return symbol switch
        {
            IMethodSymbol { Name: "GetArg" } => "a hook argument (HookCall.GetArg)",
            IPropertySymbol { Name: "Result" } => "a hook result (HookCall.Result)",
            _ => null,
        };
    }

    private sealed class CoreTypes
    {
        public INamedTypeSymbol RValue = null!;
        public INamedTypeSymbol Instance = null!;
        public INamedTypeSymbol HookCall = null!;
        public INamedTypeSymbol? Values;
        public INamedTypeSymbol? Game;
        public INamedTypeSymbol? Hooks;
        public INamedTypeSymbol? TestHost;
        public INamedTypeSymbol? GameDraw;

        public static CoreTypes? From(Compilation compilation)
        {
            var rvalue = compilation.GetTypeByMetadataName("CoreLoader.RValue");
            var instance = compilation.GetTypeByMetadataName("CoreLoader.Instance");
            var hookCall = compilation.GetTypeByMetadataName("CoreLoader.HookCall");
            if (rvalue is null || instance is null || hookCall is null) return null;
            return new CoreTypes
            {
                RValue = rvalue,
                Instance = instance,
                HookCall = hookCall,
                Values = compilation.GetTypeByMetadataName("CoreLoader.Values"),
                Game = compilation.GetTypeByMetadataName("CoreLoader.Game"),
                Hooks = compilation.GetTypeByMetadataName("CoreLoader.Hooks"),
                TestHost = compilation.GetTypeByMetadataName("CoreLoader.TestHost"),
                GameDraw = compilation.GetTypeByMetadataName("CoreLoader.GameDraw"),
            };
        }
    }
}
