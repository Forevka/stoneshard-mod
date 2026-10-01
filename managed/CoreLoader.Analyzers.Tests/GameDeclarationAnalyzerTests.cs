using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace CoreLoader.Analyzers.Tests;

public class GameDeclarationAnalyzerTests
{
    private const string Mod = "[assembly: CoreModInfo(typeof(M), \"M\", \"1.0.0\", \"Me\")]\nclass M : CoreMod { }\n";

    // A mod's assembly attributes, compiled against CoreLoader.dll and, when
    // `interop` names a game, a stand-in "<interop>.Interop" assembly.
    private static async Task<string[]> Run(string source, string? interop = null, string? interopNamespace = null)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(RValue).Assembly.Location));
        if (interop != null)
            refs = refs.Append(CSharpCompilation.Create(interop + ".Interop",
                    [CSharpSyntaxTree.ParseText($"namespace {interopNamespace ?? interop} {{ public static class Scripts {{ }} }}")],
                    refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .ToMetadataReference());
        var compilation = CSharpCompilation.Create("Snippet",
            [CSharpSyntaxTree.ParseText("using CoreLoader;\n" + source)],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "snippet does not compile: " + string.Join("; ", errors.Select(e => e.ToString())));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new GameDeclarationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.Select(d => d.Id).ToArray();
    }

    [Fact]
    public async Task ModWithoutDeclaration_IsReported()
    {
        Assert.Equal(["CL0004"], await Run(Mod));
    }

    [Theory]
    [InlineData("[assembly: CoreModGame(\"StoneShard\")]")]
    [InlineData("[assembly: CoreModGame(\"StoneShard\", \"Dwarf Eats Mountain\")]")]
    [InlineData("[assembly: CoreModAnyGame]")]
    public async Task DeclaredMod_IsClean(string declaration)
    {
        Assert.Empty(await Run(declaration + "\n" + Mod));
    }

    [Theory]
    [InlineData("[assembly: CoreModGame(\"StoneShard\")]\n[assembly: CoreModAnyGame]")]
    [InlineData("[assembly: CoreModGame()]")]
    [InlineData("[assembly: CoreModGame(\"\", \" \")]")]
    [InlineData("[assembly: CoreModGame(null)]")]
    public async Task DeclarationTheLoaderRefuses_IsReported(string declaration)
    {
        Assert.Equal(["CL0004"], await Run(declaration + "\n" + Mod, "StoneShard"));
    }

    [Fact]
    public async Task OtherInteropAssembly_IsIgnored()
    {
        // Named *.Interop, but not a generated game interop (no <name>.Scripts).
        Assert.Empty(await Run("[assembly: CoreModAnyGame]\n" + Mod, "Vendor", interopNamespace: "Elsewhere"));
    }

    [Fact]
    public async Task LibraryWithoutModInfo_IsClean()
    {
        Assert.Empty(await Run("public static class Helpers { }"));
    }

    [Theory]
    [InlineData("[assembly: CoreModGame(\"Dwarf Eats Mountain\")]", "Dwarf_Eats_Mountain")]
    [InlineData("[assembly: CoreModGame(\"StoneShard\", \"Dwarf Eats Mountain\")]", "Dwarf_Eats_Mountain")]
    [InlineData("[assembly: CoreModGame(\"stoneshard\")]", "StoneShard")]
    public async Task InteropGameNamed_IsClean(string declaration, string interop)
    {
        Assert.Empty(await Run(declaration + "\n" + Mod, interop));
    }

    [Theory]
    [InlineData("[assembly: CoreModGame(\"StoneShard\")]", "Dwarf_Eats_Mountain")]
    [InlineData("[assembly: CoreModAnyGame]", "StoneShard")]
    public async Task InteropGameNotNamed_IsReported(string declaration, string interop)
    {
        Assert.Equal(["CL0005"], await Run(declaration + "\n" + Mod, interop));
    }

    [Fact]
    public async Task InteropWithoutDeclaration_ReportsOnlyTheMissingDeclaration()
    {
        Assert.Equal(["CL0004"], await Run(Mod, "StoneShard"));
    }
}
