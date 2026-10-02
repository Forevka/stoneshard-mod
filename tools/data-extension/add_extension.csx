// add_extension.csx - register a native DLL as a GameMaker extension inside a
// game's data.win, so the game's *own* extension loader LoadLibrary's it at
// start-up (the same mechanism that already loads Steamworks_x64.dll, FastAstar
// and ImGui_GM in our test games). No proxy DLL, no injection, no exe patching.
//
// Run headless through UndertaleModCli (part of UndertaleModTool):
//   UndertaleModCli load <in.win> -s add_extension.csx -o <out.win> -f
//
// UndertaleModCli has no channel for passing arguments to a script, so the
// parameters arrive as environment variables (Install-Extension.ps1 sets them):
//   CL_EXT_NAME  extension name              (default "CoreLoaderExt")
//   CL_EXT_DLL   dll filename as GM sees it  (default "coreloader_ext.dll")
//   CL_EXT_FUNC  exported symbol to bind to  (default "coreloader_extension_probe")
//
// The script is idempotent: re-running it on an already-patched file is a no-op.

using System;
using System.Linq;
using UndertaleModLib;         // UndertaleSimpleList<>, UndertalePointerList<>
using UndertaleModLib.Models;  // UndertaleExtension*, UndertaleString, enums

string extName  = Environment.GetEnvironmentVariable("CL_EXT_NAME")  ?? "CoreLoaderExt";
string dllName  = Environment.GetEnvironmentVariable("CL_EXT_DLL")   ?? "coreloader_ext.dll";
string funcName = Environment.GetEnvironmentVariable("CL_EXT_FUNC")  ?? "coreloader_extension_probe";

if (Data.Extensions.Any(e => e.Name?.Content == extName))
{
    ScriptMessage($"Extension '{extName}' already present - nothing to do.");
    return;
}

UndertaleString S(string s) => Data.Strings.MakeString(s ?? "");

// Match the calling-convention/kind fields to whatever this game's GameMaker
// build already uses for its own extension functions, instead of hardcoding
// version-specific magic numbers. Fall back to 11 (the long-standing DLL
// convention) only if the game declares no extension functions at all.
uint fnKind = 11;
var sampleFn = Data.Extensions
    .SelectMany(e => e.Files)
    .SelectMany(f => f.Functions)
    .FirstOrDefault();
if (sampleFn != null)
    fnKind = sampleFn.Kind;

// A function id that cannot collide with any the game already defines.
uint nextId = 1;
var ids = Data.Extensions
    .SelectMany(e => e.Files)
    .SelectMany(f => f.Functions)
    .Select(fn => fn.ID)
    .ToList();
if (ids.Count > 0)
    nextId = ids.Max() + 1;

var probe = new UndertaleExtensionFunction
{
    Name      = S(funcName),   // GML-facing name (never called from GML)
    ExtName   = S(funcName),   // the exported symbol GetProcAddress resolves
    ID        = nextId,
    Kind      = fnKind,
    RetType   = UndertaleExtensionVarType.Double,
    Arguments = new UndertaleSimpleList<UndertaleExtensionFunctionArg>(),
};

var file = new UndertaleExtensionFile
{
    Filename      = S(dllName),
    CleanupScript = S(""),
    InitScript    = S(""),
    Kind          = UndertaleExtensionKind.Dll,
    Functions     = new UndertalePointerList<UndertaleExtensionFunction>(),
};
file.Functions.Add(probe);

var ext = new UndertaleExtension
{
    FolderName = S(""),        // loose file next to the exe, no subfolder
    Name       = S(extName),
    ClassName  = S(""),
    Files      = new UndertalePointerList<UndertaleExtensionFile>(),
};
ext.Files.Add(file);

Data.Extensions.Add(ext);

// GameMaker 2022.6+ stores one 16-byte product-id GUID per extension at the tail
// of the EXTN chunk. UndertaleModLib writes whatever is in productIdData back
// verbatim, so on those games we must add a GUID for the new extension - and on
// older games, where the list is empty, we must NOT, or the chunk gains 16 bytes
// the reader never expects and the file is corrupt. The list having exactly one
// entry per pre-existing extension is the reliable "this game uses them" signal.
var extnChunk = Data.FORM.Chunks["EXTN"] as UndertaleChunkEXTN;
if (extnChunk != null && extnChunk.productIdData.Count == Data.Extensions.Count - 1)
{
    extnChunk.productIdData.Add(Guid.NewGuid().ToByteArray());
    ScriptMessage($"Added a product-id GUID (EXTN now holds {extnChunk.productIdData.Count} " +
                  $"for {Data.Extensions.Count} extensions).");
}

ScriptMessage(
    $"Added extension '{extName}' -> {dllName} " +
    $"(function '{funcName}', id {nextId}, kind {fnKind}). " +
    $"Total extensions now: {Data.Extensions.Count}.");
