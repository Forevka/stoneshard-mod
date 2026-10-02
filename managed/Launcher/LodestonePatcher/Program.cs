// LodestonePatcher - edits a GameMaker data.win to register (or remove) the
// Lodestone loader as a native DLL extension, so the game's own extension loader
// loads it at start-up. Invoked by lodestone_launcher.exe as a separate process.
//
// Copyright (C) 2026 Lodestone contributors.
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License version 3, as published by the Free
// Software Foundation. It links UndertaleModLib (c) UnderminersTeam, GPL-3.0.
// See the LICENSE file in this directory. There is NO WARRANTY.
//
// Commands (parameters after the verb, as --key value):
//   info      --game <dir>|--data <path>
//   install   --game <dir>|--data <path> [--ext NAME] [--dll NAME] [--func NAME]
//   uninstall --game <dir>|--data <path> [--ext NAME]
// Exit codes: 0 ok, 1 failure, 2 bad usage.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;

const string DefaultExt  = "CoreLoaderExt";
const string DefaultDll  = "coreloader_ext.dll";
const string DefaultFunc = "coreloader_extension_probe";

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: LodestonePatcher <info|install|uninstall> --game <dir> [options]");
    return 2;
}

string cmd = args[0].ToLowerInvariant();
Dictionary<string, string> opt;
try { opt = ParseOptions(args.Skip(1)); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

try
{
    switch (cmd)
    {
        case "info":      return Info();
        case "install":   return Install();
        case "uninstall": return Uninstall();
        default:
            Console.Error.WriteLine($"unknown command '{cmd}'");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

// -------------------------------------------------------------------- commands

int Info()
{
    // Full read so string references resolve (the onlyGeneralInfo fast path leaves
    // names blank because the string chunk is not loaded) and so we can report the
    // install state from EXTN. This is a debug/scripting helper; the launcher reads
    // the game name itself from GEN8 and checks install state natively, so it never
    // pays this cost on the hot path.
    string extName = opt.GetValueOrDefault("ext", DefaultExt);
    var data = ReadData(ResolveData(), onlyGeneralInfo: false);
    var gi = data.GeneralInfo;
    Console.WriteLine("NAME=" + (gi?.DisplayName?.Content ?? gi?.Name?.Content ?? ""));
    Console.WriteLine("FILE=" + (gi?.Name?.Content ?? ""));
    Console.WriteLine($"VERSION={gi?.Major}.{gi?.Minor}.{gi?.Release}.{gi?.Build}");
    Console.WriteLine("INSTALLED=" + (data.Extensions.Any(e => e.Name?.Content == extName) ? "true" : "false"));
    return 0;
}

int Install()
{
    string path = ResolveData();
    string extName  = opt.GetValueOrDefault("ext",  DefaultExt);
    string dllName  = opt.GetValueOrDefault("dll",  DefaultDll);
    string funcName = opt.GetValueOrDefault("func", DefaultFunc);

    var data = ReadData(path, onlyGeneralInfo: false);
    if (data.Extensions.Any(e => e.Name?.Content == extName))
    {
        Console.WriteLine($"already installed: {extName}");
        return 0;
    }

    AddExtension(data, extName, dllName, funcName);
    WriteData(path, data);
    Console.WriteLine($"installed {extName} -> {dllName} (fn {funcName})");
    return 0;
}

int Uninstall()
{
    string path = ResolveData();
    string extName = opt.GetValueOrDefault("ext", DefaultExt);

    var data = ReadData(path, onlyGeneralInfo: false);
    int idx = -1;
    for (int i = 0; i < data.Extensions.Count; i++)
        if (data.Extensions[i].Name?.Content == extName) { idx = i; break; }

    if (idx < 0)
    {
        Console.WriteLine($"not installed: {extName}");
        return 0;
    }

    int countBefore = data.Extensions.Count;
    data.Extensions.RemoveAt(idx);

    // Keep the 2022.6+ product-id list one-per-extension (see AddExtension).
    var extn = data.FORM.EXTN;
    if (extn != null && extn.productIdData.Count == countBefore && idx < extn.productIdData.Count)
        extn.productIdData.RemoveAt(idx);

    WriteData(path, data);
    Console.WriteLine($"uninstalled {extName}");
    return 0;
}

// --------------------------------------------------------------------- helpers

// Mirrors tools/data-extension/add_extension.csx, calling the API directly.
void AddExtension(UndertaleData data, string extName, string dllName, string funcName)
{
    UndertaleString S(string s) => data.Strings.MakeString(s ?? "");

    // Match the calling convention the game already uses for its own extension
    // functions instead of hardcoding a version-specific constant; fall back to
    // 11 (the long-standing DLL convention) only if the game declares none.
    uint fnKind = 11;
    var sampleFn = data.Extensions
        .SelectMany(e => e.Files)
        .SelectMany(f => f.Functions)
        .FirstOrDefault();
    if (sampleFn != null)
        fnKind = sampleFn.Kind;

    uint nextId = 1;
    var ids = data.Extensions
        .SelectMany(e => e.Files)
        .SelectMany(f => f.Functions)
        .Select(fn => fn.ID)
        .ToList();
    if (ids.Count > 0)
        nextId = ids.Max() + 1;

    var probe = new UndertaleExtensionFunction
    {
        Name      = S(funcName),
        ExtName   = S(funcName),
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
        FolderName = S(""),
        Name       = S(extName),
        ClassName  = S(""),
        Files      = new UndertalePointerList<UndertaleExtensionFile>(),
    };
    ext.Files.Add(file);

    data.Extensions.Add(ext);

    // GameMaker 2022.6+ stores one 16-byte product-id GUID per extension at the
    // tail of EXTN; add one only when the game already keeps one per extension,
    // or older files (no product ids) would gain bytes the reader never expects.
    var extn = data.FORM.EXTN;
    if (extn != null && extn.productIdData.Count == data.Extensions.Count - 1)
        extn.productIdData.Add(Guid.NewGuid().ToByteArray());
}

string ResolveData()
{
    if (opt.TryGetValue("data", out var explicitPath))
    {
        if (!File.Exists(explicitPath)) throw new FileNotFoundException("data file not found: " + explicitPath);
        return explicitPath;
    }
    if (!opt.TryGetValue("game", out var gameDir))
        throw new ArgumentException("--game <dir> or --data <path> is required");
    if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException("game folder not found: " + gameDir);

    var candidate = Path.Combine(gameDir, "data.win");
    if (File.Exists(candidate)) return candidate;
    var win = Directory.EnumerateFiles(gameDir, "*.win").FirstOrDefault();
    return win ?? throw new FileNotFoundException("no data.win (or *.win) under " + gameDir);
}

static UndertaleData ReadData(string path, bool onlyGeneralInfo)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
    // A warning handler must be supplied or UndertaleModLib throws on the first
    // warning. The onlyGeneralInfo path deliberately stops mid-GEN8, which raises
    // an expected "read only N of M (padding?)" warning; these games also load
    // cleanly in the full path. We ignore warnings - the reader self-corrects
    // (e.g. skips the padding) - and rely on hard IOExceptions for real corruption.
    return UndertaleIO.Read(fs, (_, _) => { }, null, onlyGeneralInfo);
}

static void WriteData(string path, UndertaleData data)
{
    // Write to a sibling temp file, then atomically move it over the target, so a
    // crash, power loss or full disk mid-write can never truncate the real data.win.
    // This matters for direct `--data` use where the launcher made no backup.
    string tmp = path + ".lodestone-tmp";
    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        UndertaleIO.Write(fs, data);
    File.Move(tmp, path, overwrite: true);
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> rest)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var list = rest.ToList();
    for (int i = 0; i < list.Count; i++)
    {
        var a = list[i];
        if (!a.StartsWith("--")) throw new ArgumentException($"unexpected argument '{a}' (expected --key value)");
        var key = a.Substring(2);
        if (i + 1 >= list.Count) throw new ArgumentException($"missing value for --{key}");
        map[key] = list[++i];
    }
    return map;
}
