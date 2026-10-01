; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CL0001 | CoreLoader.Lifetime | Warning | A game value (RValue) kept in a field or auto-property
CL0002 | CoreLoader.Lifetime | Warning | An Instance or HookCall kept past its call (field, or a stored lambda capturing a HookCall)
CL0003 | CoreLoader.Lifetime | Warning | Values.Free on a value read from HookCall.GetArg or Result
CL0004 | CoreLoader.Games | Error | A mod assembly with [CoreModInfo] but neither [CoreModGame] nor [CoreModAnyGame]
CL0005 | CoreLoader.Games | Warning | A mod compiled against <Game>.Interop whose [CoreModGame] does not name that game, or that declares [CoreModAnyGame]
