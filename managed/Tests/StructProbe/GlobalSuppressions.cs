// Deliberate exceptions to the CoreLoader lifetime analyzer (CL0001-CL0003),
// each with the reason the value is safe to keep.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~F:StructProbe.Probe._kept",
    Justification = "The probe tests exactly this: a struct kept with Values.Keep across frames, freed at the end.")]
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~F:StructProbe.Probe._copy",
    Justification = "A Values.Copy of the kept struct, freed at the end of the probe.")]
