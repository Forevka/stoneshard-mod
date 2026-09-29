// Names every compiled GML function from the exe's own symbol table, then
// decompiles the game-logic ones to one .c file per function.
//
// Headless:
//   analyzeHeadless <projDir> <projName> -process <Game>.exe -readOnly -noanalysis
//     -scriptPath tools/re/ghidra -postScript ExportGml.java <symtab.json> <builtins.json> <outDir>
//
// symtab.json   {"gml_Script_foo": "0x140001000", ...}   (relib.table())
// builtins.json tools/re/builtin_table.json               ({"name": {"func": "0x..."}})
// Files already present in outDir are skipped, so an interrupted run resumes.
// Optional 4th argument: per-function decompile timeout in seconds (default 120);
// delete the "// decompile failed" files and rerun with a larger one.
// Optional 5th argument "seq": decompile one function at a time instead of in parallel.
//@category CoreLoader

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

import ghidra.app.decompiler.*;
import ghidra.app.decompiler.parallel.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.SourceType;
import ghidra.util.task.TaskMonitor;

public class ExportGml extends GhidraScript {

    private static final Pattern SYM = Pattern.compile("\"([^\"]+)\"\\s*:\\s*\"(0x[0-9a-fA-F]+)\"");
    private static final Pattern BUILTIN = Pattern.compile("\"([^\"]+)\"\\s*:\\s*\\{\\s*\"func\"\\s*:\\s*\"(0x[0-9a-fA-F]+)\"");

    private Map<String, Long> parse(String path, Pattern p) throws IOException {
        String s = Files.readString(Path.of(path), StandardCharsets.UTF_8);
        Map<String, Long> out = new LinkedHashMap<>();
        Matcher m = p.matcher(s);
        while (m.find()) out.put(m.group(1), Long.decode(m.group(2)));
        return out;
    }

    private Function nameAt(String name, long va) {
        try {
            return nameAtUnchecked(name.replaceAll("[^A-Za-z0-9_.$@]", "_"), va);
        } catch (Exception e) {
            printerr("cannot name " + name + " @ 0x" + Long.toHexString(va) + ": " + e.getMessage());
            return null;
        }
    }

    private Function nameAtUnchecked(String name, long va) throws Exception {
        Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(va);
        Function f = getFunctionAt(a);
        if (f == null) {
            disassemble(a);
            f = createFunction(a, name);
        }
        if (f == null) return null;
        try {
            f.setName(name, SourceType.USER_DEFINED);
        } catch (Exception e) {
            // Duplicate (two names on one body): keep the first as the function name.
            createLabel(a, name, false);
        }
        return f;
    }

    private static boolean wanted(String name) {
        return name.startsWith("gml_Script_") || name.startsWith("gml_Object_");
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("usage: ExportGml.java <symtab.json> <builtins.json> <outDir>");
            return;
        }
        Path outDir = Path.of(args[2]);
        Files.createDirectories(outDir);

        Map<String, Long> syms = parse(args[0], SYM);
        Map<String, Long> builtins = parse(args[1], BUILTIN);
        println("symbols " + syms.size() + ", builtins " + builtins.size());

        // Builtins first so decompiled GML shows e.g. bi_instance_create at call sites.
        for (Map.Entry<String, Long> e : builtins.entrySet()) {
            if (monitor.isCancelled()) return;
            nameAt("bi_" + e.getKey(), e.getValue());
        }

        List<Function> todo = new ArrayList<>();
        Map<Function, String> names = new HashMap<>();
        int named = 0;
        for (Map.Entry<String, Long> e : syms.entrySet()) {
            if (monitor.isCancelled()) return;
            Function f = nameAt(e.getKey(), e.getValue());
            if (f == null) continue;
            named++;
            if (!wanted(e.getKey())) continue;
            if (Files.exists(outDir.resolve(e.getKey() + ".c"))) continue;
            if (names.putIfAbsent(f, e.getKey()) == null) todo.add(f);
        }
        println("named " + named + ", decompiling " + todo.size());

        // Address index for grepping by VA later.
        try (PrintWriter w = new PrintWriter(Files.newBufferedWriter(outDir.resolve("_index.tsv")))) {
            for (Map.Entry<String, Long> e : syms.entrySet())
                w.printf("%s\t0x%x%n", e.getKey(), e.getValue());
        }

        final int[] done = {0};
        final long t0 = System.currentTimeMillis();
        DecompilerCallback<Void> cb = new DecompilerCallback<>(currentProgram, new DecompileConfigurer() {
            @Override
            public void configure(DecompInterface d) {
                d.setOptions(new DecompileOptions());
                d.toggleCCode(true);
                d.toggleSyntaxTree(false);
                d.setSimplificationStyle("decompile");
            }
        }) {
            @Override
            public Void process(DecompileResults r, TaskMonitor m) throws Exception {
                Function f = r.getFunction();
                String name = names.get(f);
                StringBuilder sb = new StringBuilder();
                sb.append("// ").append(name).append(" @ ").append(f.getEntryPoint()).append('\n');
                if (r.decompileCompleted() && r.getDecompiledFunction() != null)
                    sb.append(r.getDecompiledFunction().getC());
                else
                    sb.append("// decompile failed: ").append(r.getErrorMessage()).append('\n');
                Files.writeString(outDir.resolve(name + ".c"), sb.toString(), StandardCharsets.UTF_8);
                int n;
                synchronized (done) { n = ++done[0]; }
                if (n % 250 == 0) {
                    long s = (System.currentTimeMillis() - t0) / 1000;
                    println(String.format("%d/%d  %ds", n, todo.size(), s));
                }
                return null;
            }
        };
        int timeout = args.length > 3 ? Integer.parseInt(args[3]) : 120;
        if (args.length > 4 && args[4].equals("seq")) {
            // One decompiler process at a time: the huge event handlers each
            // want several GB, and running them side by side exhausts RAM.
            cb.dispose();
            DecompInterface d = new DecompInterface();
            d.setOptions(new DecompileOptions());
            d.toggleCCode(true);
            d.toggleSyntaxTree(false);
            d.setSimplificationStyle("decompile");
            d.openProgram(currentProgram);
            try {
                for (Function f : todo) {
                    if (monitor.isCancelled()) break;
                    long s = System.currentTimeMillis();
                    DecompileResults r = d.decompileFunction(f, timeout, monitor);
                    cb.process(r, monitor);
                    println(String.format("%s  %ds  %s", names.get(f),
                        (System.currentTimeMillis() - s) / 1000, r.decompileCompleted() ? "ok" : "FAILED"));
                    if (!r.decompileCompleted()) {
                        // A timed-out process is dead; start a fresh one.
                        d.dispose();
                        d = new DecompInterface();
                        d.setOptions(new DecompileOptions());
                        d.toggleCCode(true);
                        d.toggleSyntaxTree(false);
                        d.setSimplificationStyle("decompile");
                        d.openProgram(currentProgram);
                    }
                }
            } finally {
                d.dispose();
            }
            println("done " + done[0]);
            return;
        }
        cb.setTimeout(timeout);
        try {
            ParallelDecompiler.decompileFunctions(cb, todo, monitor);
        } finally {
            cb.dispose();
        }
        println("done " + done[0]);
    }
}
