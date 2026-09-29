// Deliberate exceptions to the CoreLoader lifetime analyzer (CL0001-CL0003),
// each with the reason the value is safe to keep.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0002", Scope = "member", Target = "~F:StoneshardCheats.Player._self",
    Justification = "Refreshed by o_player's Step event and trusted only while that keeps running and instance_exists(_id) holds; see Player.Current.")]
[assembly: SuppressMessage("CoreLoader.Lifetime", "CL0001", Scope = "member", Target = "~F:StoneshardCheats.Player._id",
    Justification = "An instance id: a number or a plain reference, never a pooled string, array or struct.")]
