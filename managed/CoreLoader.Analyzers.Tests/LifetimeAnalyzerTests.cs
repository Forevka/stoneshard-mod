using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace CoreLoader.Analyzers.Tests;

public class LifetimeAnalyzerTests
{
    // Every snippet is a mod file: it compiles against CoreLoader.dll and the
    // framework, and the test asserts on the analyzer's diagnostics only.
    private static async Task<string[]> Run(string source)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(RValue).Assembly.Location));
        var compilation = CSharpCompilation.Create("Snippet",
            [CSharpSyntaxTree.ParseText("using System; using System.Collections.Generic; using System.Linq; using System.Threading.Tasks; using CoreLoader;\n" + source)],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "snippet does not compile: " + string.Join("; ", errors.Select(e => e.ToString())));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.Id).ToArray();
    }

    [Theory]
    [InlineData("RValue _v;")]
    [InlineData("static RValue s_v;")]
    [InlineData("RValue? _v;")]
    [InlineData("RValue[] _a = [];")]
    [InlineData("List<RValue> _l = new();")]
    [InlineData("Dictionary<string, (int, RValue)> _d = new();")]
    [InlineData("public RValue Value { get; set; }")]
    [InlineData("public static RValue Value { get; private set; }")]
    public async Task StoredRValue_IsReported(string member)
    {
        Assert.Equal(["CL0001"], await Run($"class M {{ {member} }}"));
    }

    [Fact]
    public async Task RecordPositionalRValue_IsReported()
    {
        Assert.Equal(["CL0001"], await Run("record R(RValue Value);"));
    }

    [Theory]
    [InlineData("RValue Now => Game.CallBuiltin(\"x\");")]
    [InlineData("RValue Now { get { return RValue.Undefined; } }")]
    [InlineData("double _real; string _text = \"\"; InstanceRef _ref;")]
    [InlineData("void F(HookCall c) { RValue v = c.GetArg(0); double d = v.AsReal; }")]
    public async Task NonStoringMembers_AreClean(string member)
    {
        Assert.Empty(await Run($"class M {{ {member} }}"));
    }

    [Theory]
    [InlineData("Instance _i;")]
    [InlineData("static Instance? _i;")]
    [InlineData("List<Instance> _all = new();")]
    [InlineData("HookCall _c;")]
    [InlineData("public HookCall Last { get; set; }")]
    public async Task StoredInstanceOrHookCall_IsReported(string member)
    {
        Assert.Equal(["CL0002"], await Run($"class M {{ {member} }}"));
    }

    [Theory]
    [InlineData("_later = () => c.GetArg(0);")]
    [InlineData("Game.RunOnGameThread(() => c.SetArg(0, RValue.FromReal(1)));")]
    [InlineData("Task.Run(() => c.ArgCount);")]
    [InlineData("_queue.Add(() => c.GetArg(0));")]
    [InlineData("_later = new Func<object>(() => c.Result);")]
    public async Task StoredLambdaCapturingHookCall_IsReported(string statement)
    {
        var src = "class M { Func<object>? _later; List<Func<object>> _queue = new(); void H(HookCall c) { " + statement + " } }";
        Assert.Equal(["CL0002"], await Run(src));
    }

    [Theory]
    // Used on the spot: nothing outlives the handler.
    [InlineData("var n = new[] { 1, 2 }.Count(x => x < c.ArgCount);")]
    // The lambda's own HookCall parameter, as in Hooks.Before(name, call => ...).
    [InlineData("_later = (Func<HookCall, object>)(call => call.ArgCount);")]
    // A copy of the data, not the call.
    [InlineData("var n = c.ArgCount; Game.RunOnGameThread(() => Console.WriteLine(n));")]
    public async Task LambdaNotKeepingHookCall_IsClean(string statement)
    {
        var src = "class M { object? _later; void H(HookCall c) { " + statement + " } }";
        Assert.Empty(await Run(src));
    }

    [Theory]
    [InlineData("var a = c.GetArg(0); Values.Free(ref a);")]
    [InlineData("RValue r; r = c.Result; Values.Free(ref r);")]
    [InlineData("var a = (c.GetArg(1)); Values.Free(ref a);")]
    public async Task FreeingALentValue_IsReported(string statement)
    {
        Assert.Equal(["CL0003"], await Run("class M { void H(HookCall c) { " + statement + " } }"));
    }

    [Fact]
    public async Task FreeingInALambdaHandler_IsReported()
    {
        var src = "class M { void Init() { Action<HookCall> h = call => { var a = call.GetArg(0); Values.Free(ref a); }; } }";
        Assert.Equal(["CL0003"], await Run(src));
    }

    [Theory]
    [InlineData("var a = Values.Keep(c.GetArg(0)); Values.Free(ref a);")]
    [InlineData("var a = Values.Copy(c.Result); Values.Free(ref a);")]
    [InlineData("var a = RValue.FromString(\"x\"); Values.Free(ref a);")]
    public async Task FreeingAnOwnedValue_IsClean(string statement)
    {
        Assert.Empty(await Run("class M { void H(HookCall c) { " + statement + " } }"));
    }

    [Fact]
    public async Task Suppression_IsHonoured()
    {
        var src = "class M {\n#pragma warning disable CL0001 // kept with Values.Keep\nRValue _v;\n#pragma warning restore CL0001\n}";
        Assert.Empty(await Run(src));
    }
}
