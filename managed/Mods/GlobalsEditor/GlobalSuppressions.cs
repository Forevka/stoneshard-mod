// Deliberate exceptions to the CoreLoader lifetime analyzer (CL0001-CL0003),
// each with the reason the value is safe to keep.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~F:GlobalsEditor.GlobalsEditorMod._frozen",
    Justification = "Only numbers (and bools) are frozen: DrawRow never offers a freeze for a string, array or struct, and numbers hold no reference.")]
