// Deliberate exceptions to the CoreLoader lifetime analyzer (CL0001-CL0003),
// each with the reason the value is safe to keep.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~P:CoreConsole.Evaluator.Ans",
    Justification = "ans holds its own reference: Evaluator.Run stores a Values.Copy and frees the previous one, and Release frees it on shutdown.")]
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~F:CoreConsole.Freezer.Entry.Value",
    Justification = "Frozen values are taken with Values.Keep in Freeze and freed when the entry is removed (Unfreeze, Clear, or the instance going away).")]
