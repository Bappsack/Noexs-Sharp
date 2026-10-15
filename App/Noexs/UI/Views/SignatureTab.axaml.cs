using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.Misc;
using Noexs.Util;

namespace Noexs.UI.Views;

/// <summary>
/// Creates ARM64 code signatures (AOBs) for an address and finds them again, e.g. after a game
/// update, resolving the global addresses the matched code references (adrp + ldr/add/str).
/// </summary>
public partial class SignatureTab : UserControl, IController
{
    /// <summary>A list row; <see cref="Address"/> is set for rows that point somewhere.</summary>
    public sealed record Row(string Text, long? Address = null, string? Expression = null)
    {
        public override string ToString() => Text;
    }

    private const string AllModules = "all modules";
    private const int MaxListedMatches = 100;

    private MainWindow mc = null!;
    private readonly ObservableCollection<Row> details = new();
    private readonly ObservableCollection<Row> results = new();
    private SignatureMaker.Result? created;
    private string createdModule = "main";

    // the last module code read, so Create followed by Find doesn't read it twice
    private sealed record CodeCache(long Pid, long Start, byte[] Code);

    private volatile CodeCache? cache;

    public SignatureTab()
    {
        InitializeComponent();
        maxInstructions.Value = 32;
        createDetails.ItemsSource = details;
        candidateBox.SelectionChanged += (_, _) =>
        {
            if (candidateBox.SelectedItem is Candidate c && !ReferenceEquals(c.Result, created))
            {
                ShowCandidate(c);
            }
        };
        findResults.ItemsSource = results;
        findModule.ItemsSource = new[] { "main", AllModules, "rtld", "sdk" }
            .Concat(Enumerable.Range(0, 8).Select(i => "subsdk" + i)).ToList();
        findModule.SelectedIndex = 0;
        createDetails.ContextMenu = RowMenu(createDetails);
        findResults.ContextMenu = RowMenu(findResults);
        createDetails.DoubleTapped += (_, _) => Disassemble(createDetails.SelectedItem as Row);
        findResults.DoubleTapped += (_, _) => Disassemble(findResults.SelectedItem as Row);
        createAddress.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OnCreate(null, new RoutedEventArgs());
            }
        };
        findSignature.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OnFind(null, new RoutedEventArgs());
            }
        };
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
    }

    public void OnConnect()
    {
        createPane.IsEnabled = true;
        findPane.IsEnabled = true;
    }

    public void OnDisconnect()
    {
        createPane.IsEnabled = false;
        findPane.IsEnabled = false;
        cache = null;
    }

    /// <summary>Used by the Disassembler's "Create Signature" menu.</summary>
    public void CreateAt(long address)
    {
        createAddress.Text = HexUtils.FormatAddress(address);
        OnCreate(null, new RoutedEventArgs());
    }

    /// <summary>Used by Pointer Search: signatures for the code that uses the path's main base.</summary>
    public void CreateForMainOffset(long mainOffset, string path)
    {
        createAddress.Text = path;
        CreateForPath(path);
    }

    /// <summary>The innermost module base of a pointer path, e.g. "main+4741FA0" in [[main+4741FA0]+198]+30.</summary>
    private static readonly System.Text.RegularExpressions.Regex PathBase = new(
        @"\b(main|sdk|rtld|subsdk\d+)\+([0-9A-Fa-f]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Removes spaces and "0x" prefixes, so the expression evaluator (hex without 0x) accepts it.</summary>
    private static string NormalizePath(string text)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"(?<![0-9A-Fa-f])0[xX](?=[0-9A-Fa-f])", "");
    }

    /// <summary>Signatures for the code that uses the base of a pointer path; the rest of the path becomes the recipe.</summary>
    private void CreateForPath(string text)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        string path = NormalizePath(text);
        System.Text.RegularExpressions.Match m = PathBase.Match(path);
        if (!m.Success)
        {
            MessageDialog.ShowMessage("The pointer path needs a module base such as main+4741FA0, e.g. [[[[main+4741FA0]+198]+30]+9D0]",
                AlertType.WARNING);
            return;
        }
        string moduleName = m.Groups[1].Value.ToLowerInvariant();
        long baseOffset = long.Parse(m.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        // the path with its base replaced by the address the signature's instruction computes
        string template = path[..m.Index] + RefPlaceholder + path[(m.Index + m.Length)..];
        int max = MaxInstructions();
        Run(createButton, "Creating signatures for " + path + "...", debugger =>
        {
            List<MemoryRegions.Module> modules = MemoryRegions.Modules(debugger.Query(0, 10000));
            MemoryRegions.Module module = modules.FirstOrDefault(x => x.Name == moduleName)
                ?? throw new InvalidOperationException(moduleName + " module not found; attach to a game first");
            long global = module.Start + baseOffset;
            byte[] code = ReadCode(debugger, module);
            long? expected = TryEval(debugger, path, 0, modules);
            List<Candidate> list = GlobalCandidates(code, module, modules, global, path, template, 0, max, MaxCandidates);
            list = Sort(Verify(debugger, list, modules, expected)).ToList();
            string now = expected is { } v ? " = " + HexUtils.FormatAddress(v) : " (can't be read right now)";
            string header = $"{path}{now}: base {moduleName}+0x{baseOffset:X} is used by {Plural(list.Count, "instruction")}";
            return () => ShowCandidates(header, list, $"No code in {moduleName} uses {moduleName}+0x{baseOffset:X}"
                + " (or a struct containing it) directly. Try another pointer path.");
        });
    }

    // ---- create -------------------------------------------------------------------------

    /// <summary>
    /// One way to find the address again. After the signature is found, the instruction at the
    /// target offset computes an address ("ref"); <see cref="Path"/> turns it into your address,
    /// e.g. "[ref+8]+10" (hex, like pointer paths). An empty path means the instruction itself.
    /// </summary>
    public sealed record Candidate(string Label, string Info, SignatureMaker.Result Result, string Module, List<Row> Rows,
        string Path, long RefTarget, long Delta, long PointerOffset, bool? Verified = null)
    {
        public override string ToString() => (Verified switch { true => "✓ ", false => "✗ ", _ => "" }) + Label;
    }

    private const string RefPlaceholder = "{ref}";
    private const int MaxCandidates = 12;

    /// <summary>Struct offsets considered when matching a pointer or a global struct to the address.</summary>
    private const long MaxStructOffset = 0x1000;

    private Candidate? createdCandidate;

    private int MaxInstructions() => (int)Math.Clamp(maxInstructions.Value, 4, 64);

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        string input = NormalizePath(createAddress.Text ?? "");
        if (!long.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long address))
        {
            if (input.Contains('[') || PathBase.IsMatch(input))
            {
                CreateForPath(input);
            }
            else
            {
                MessageDialog.ShowMessage("Enter a hex address (e.g. 72B8CD9000) or a pointer path (e.g. [[main+4741FA0]+198]+30).",
                    AlertType.WARNING);
            }
            return;
        }
        int max = MaxInstructions();
        Run(createButton, "Creating signature for " + HexUtils.FormatAddress(address) + "...", debugger =>
        {
            MemoryInfo[] infos = debugger.Query(0, 10000);
            List<MemoryRegions.Module> modules = MemoryRegions.Modules(infos);
            MemoryRegions.Ranges ranges = MemoryRegions.Detect(infos);
            string where = Describe(modules, ranges, address);

            // 1. an instruction: the signature is the code itself
            MemoryRegions.Module? module = modules.FirstOrDefault(m => address >= m.Start && address < m.TextEnd);
            if (module != null)
            {
                byte[] code = ReadCode(debugger, module);
                int at = (int)((address & ~3L) - module.Start);
                SignatureMaker.Reference? own = SignatureMaker.References(code, at, address & ~3L, 1)
                    .FirstOrDefault(r => r.PageInstruction != null);
                int minBack = own?.PageInstruction is { } page ? (int)(((address & ~3L) - page) / 4) : 0;
                SignatureMaker.Result result = SignatureMaker.Create(code, module.Start, address,
                    maxInstructions: Math.Max(max, Math.Min(64, minBack + 4)), minBack: minBack);
                List<Row> rows = DescribeCode(code, module, modules, result.Start, result.Instructions, address & ~3L, result.Signature);
                string offset = result.TargetOffset == 0
                    ? "It starts at your address."
                    : $"It starts 0x{result.TargetOffset:X} bytes before your address (target offset 0x{result.TargetOffset:X}).";
                var c = new Candidate("code at " + where, ResultText(result, module.Name) + " " + offset, result, module.Name, rows,
                    "", 0, 0, 0, true);
                return () => ShowCandidates(where + " is code", [c], "");
            }

            // 2. a global variable in a module's data: the code that uses it
            module = modules.FirstOrDefault(m => m.Contains(address));
            if (module != null)
            {
                MemoryRegions.Module m = module;
                byte[] code = ReadCode(debugger, m);
                List<Candidate> list = GlobalCandidates(code, m, modules, address, where, RefPlaceholder, 0, max, MaxCandidates);
                list = Verify(debugger, list, modules, address);
                return () => ShowCandidates($"{where} is data in {m.Name}, used by {Plural(list.Count, "instruction")}", list,
                    $"No code in {m.Name} uses {where} or a struct containing it directly.");
            }

            // 3. heap (or other runtime memory): pointers in main's data to it, then the code using them
            MemoryRegions.Module main = modules.FirstOrDefault(x => x.Name == "main")
                ?? throw new InvalidOperationException("main module not found; attach to a game first");
            byte[] data = ReadModuleData(debugger, infos, main);
            var pointers = new List<(long At, long Offset)>();
            for (int o = 0; o + 8 <= data.Length; o += 8)
            {
                // unsigned: values with the top bit set used to wrap around and pass as tiny offsets
                ulong v = BitConverter.ToUInt64(data, o);
                if (v != 0 && v <= (ulong)address && (ulong)address - v < MaxStructOffset)
                {
                    pointers.Add((main.TextEnd + o, (long)((ulong)address - v)));
                }
            }
            byte[] mainCode = ReadCode(debugger, main);
            var all = new List<Candidate>();
            foreach (var (at, off) in pointers.OrderBy(p => p.Offset).ThenBy(p => p.At).Take(8))
            {
                string template = $"[{RefPlaceholder}]+{off:X}";
                all.AddRange(GlobalCandidates(mainCode, main, modules, at, $"[{Rel(modules, at)}]+{off:X2}", template, off, max, 4));
                if (all.Count(c => c.Result.Unique) >= MaxCandidates)
                {
                    break;
                }
            }
            List<Candidate> sorted = Sort(Verify(debugger, all, modules, address)).Take(MaxCandidates).ToList();
            string header = pointers.Count == 0
                ? where
                : $"{where}: {Plural(pointers.Count, "pointer")} in main's data {(pointers.Count == 1 ? "points" : "point")} to it (offset < 0x{MaxStructOffset:X})";
            return () => ShowCandidates(header, sorted,
                pointers.Count == 0
                    ? $"Nothing in main's data points to {where} (or up to 0x{MaxStructOffset:X} before it).\n\n"
                      + "Heap objects move on every start, so a signature needs a pointer path from main. "
                      + "Run Pointer Search for this address, select a result and click \"Create Signature\" there."
                    : "Main has pointers to this address, but no code in main uses them directly. "
                      + "Use Pointer Search and create the signature from a result.");
        });
    }

    /// <summary>Signatures for the instructions that use <paramref name="global"/> (or a struct containing it).</summary>
    /// <param name="template">the path to the wanted address, with <see cref="RefPlaceholder"/> standing for the global</param>
    private static List<Candidate> GlobalCandidates(byte[] code, MemoryRegions.Module module, List<MemoryRegions.Module> modules,
        long global, string what, string template, long pointerOffset, int maxInstructions, int maxCount)
    {
        List<SignatureMaker.Reference> refs = SignatureMaker.FindReferences(code, module.Start, global - MaxStructOffset + 1, global, 4096);
        var list = new List<Candidate>();
        // exact uses first, then uses of an enclosing struct (adrp + add, then a field)
        foreach (SignatureMaker.Reference r in refs.OrderBy(r => global - r.Target).ThenBy(r => r.InstructionAddress)
                     .DistinctBy(r => r.InstructionAddress).Take(maxCount * 2))
        {
            // the adrp the instruction depends on must be part of the signature: without it the
            // instruction's address would come from whatever code precedes the match
            int minBack = r.PageInstruction is { } page ? (int)((r.InstructionAddress - page) / 4) : 0;
            SignatureMaker.Result result = SignatureMaker.Create(code, module.Start, r.InstructionAddress,
                maxInstructions: Math.Max(maxInstructions, Math.Min(64, minBack + 4)), minBack: minBack);
            long delta = global - r.Target;
            string path = template.Replace(RefPlaceholder, delta == 0 ? "ref" : $"ref+{delta:X}");
            string label = $"{what}  ← {r.Kind} at {Rel(modules, r.InstructionAddress)}"
                + (delta == 0 ? "" : $" (struct base +0x{delta:X})")
                + (result.Unique ? $"  [unique, {result.Instructions} instr]" : $"  [{result.Matches} matches]");
            string info = ResultText(result, module.Name)
                + $" Then: ref = the {r.Kind} at target offset 0x{result.TargetOffset:X} ({Rel(modules, r.Target)}), address = {path}";
            List<Row> rows = DescribeCode(code, module, modules, result.Start, result.Instructions, r.InstructionAddress, result.Signature);
            list.Add(new Candidate(label, info, result, module.Name, rows, path, r.Target, delta, pointerOffset));
            if (list.Count(c => c.Result.Unique) >= maxCount)
            {
                break;
            }
        }
        return Sort(list).Take(maxCount).ToList();
    }

    /// <summary>Evaluates each candidate's path now and checks it really gives <paramref name="expected"/>.</summary>
    private static List<Candidate> Verify(Debugger debugger, List<Candidate> list, List<MemoryRegions.Module> modules, long? expected)
    {
        return list.Select(c =>
        {
            long? now = c.Path.Length == 0 ? null : TryEval(debugger, c.Path, c.RefTarget, modules);
            bool? ok = expected == null || now == null ? null : now == expected;
            string check = now == null ? "" : $" = {HexUtils.FormatAddress(now.Value)}" + (ok == true ? " ✓" : ok == false ? " ✗ (not your address)" : "");
            return c with { Verified = ok, Info = c.Info + check };
        }).ToList();
    }

    private static IEnumerable<Candidate> Sort(IEnumerable<Candidate> list)
    {
        return list.OrderBy(c => c.Verified == false ? 1 : 0)
            .ThenBy(c => c.Result.Unique ? 0 : 1)
            .ThenBy(c => c.Delta == 0 ? 0 : 1)
            .ThenBy(c => c.PointerOffset)
            .ThenBy(c => c.Result.Instructions);
    }

    /// <summary>Evaluates a path like "[ref+8]+10" (module names such as main are available too).</summary>
    private static long? TryEval(Debugger debugger, string path, long refValue, List<MemoryRegions.Module> modules)
    {
        try
        {
            var vars = new Dictionary<string, long> { ["ref"] = refValue };
            foreach (MemoryRegions.Module m in modules)
            {
                vars.TryAdd(m.Name, m.Start);
            }
            return new ExpressionEvaluator(new PathVariables(vars), debugger.Peek64).Eval(path);
        }
        catch (Exception ex)
        {
            Log.Info("signature path \"" + path + "\": " + ex.Message);
            return null;
        }
    }

    private sealed class PathVariables(Dictionary<string, long> vars) : ExpressionEvaluator.IVariableProvider
    {
        public long Get(string name) => vars[name];

        public bool ContainsVar(string value) => vars.ContainsKey(value);
    }

    /// <summary>"main+1234" in expression syntax (hex without 0x), or the plain hex address.</summary>
    private static string ExprName(List<MemoryRegions.Module> modules, long address)
    {
        MemoryRegions.Module? m = modules.FirstOrDefault(x => x.Contains(address));
        return m == null ? address.ToString("X") : $"{m.Name}+{address - m.Start:X}";
    }

    private static string ResultText(SignatureMaker.Result result, string module)
    {
        string size = $"{result.Instructions} instructions ({result.Signature.Length} bytes, {result.Signature.WildcardCount} wildcards)";
        return result.Unique
            ? $"Unique in {module}: {size}."
            : $"Not unique: {result.Matches} matches in {module} even with {size}. Try a larger Max instr. or another candidate.";
    }

    private static string Plural(int n, string word) => n == 1 ? "1 " + word : $"{n} {word}s";

    /// <summary>"main+0x1234", "heap+0x1234" or the plain address.</summary>
    private static string Describe(List<MemoryRegions.Module> modules, MemoryRegions.Ranges ranges, long address)
    {
        if (modules.Any(m => m.Contains(address)))
        {
            return Rel(modules, address);
        }
        if (ranges.HeapStart is { } hs && ranges.HeapEnd is { } he && address >= hs && address < he)
        {
            return $"heap+0x{address - hs:X}";
        }
        return HexUtils.FormatAddress(address);
    }

    private void ShowCandidates(string header, List<Candidate> list, string none)
    {
        createWhere.Text = header;
        created = null;
        createdCandidate = null;
        candidateBox.ItemsSource = list;
        candidateBox.IsEnabled = list.Count > 1;
        if (list.Count == 0)
        {
            createdSignature.Text = "";
            createInfo.Text = "";
            details.Clear();
            mc.SetStatus("No signature for this address");
            MessageDialog.ShowMessage(none, AlertType.INFORMATION);
            return;
        }
        candidateBox.SelectedIndex = 0;
        ShowCandidate(list[0]);
        mc.SetStatus(list[0].Result.Unique ? "Signature created" : "Signature created (not unique)");
    }

    private void ShowCandidate(Candidate c)
    {
        created = c.Result;
        createdCandidate = c;
        createdModule = c.Module;
        createdSignature.Text = c.Result.Signature.ToString();
        createInfo.Text = c.Info;
        details.Clear();
        foreach (Row r in c.Rows)
        {
            details.Add(r);
        }
    }

    /// <summary>Signature, target offset and path in one line, e.g. "21 ?? ?? 91 ... | 0 | [ref+8]+10".</summary>
    private static string Recipe(Candidate c)
    {
        return c.Path.Length == 0 && c.Result.TargetOffset == 0
            ? c.Result.Signature.ToString()
            : $"{c.Result.Signature} | {c.Result.TargetOffset:X} | {c.Path}";
    }

    private async void OnCopySignature(object? sender, RoutedEventArgs e)
    {
        await Copy(createdSignature.Text);
    }

    private async void OnCopyRecipe(object? sender, RoutedEventArgs e)
    {
        if (createdCandidate != null)
        {
            await Copy(Recipe(createdCandidate));
        }
    }

    private void OnUseInFind(object? sender, RoutedEventArgs e)
    {
        if (createdCandidate is not { } c)
        {
            return;
        }
        findSignature.Text = c.Result.Signature.ToString();
        findOffset.Text = c.Result.TargetOffset.ToString("X");
        findPath.Text = c.Path;
        findModule.SelectedItem = createdModule;
        if (findModule.SelectedItem == null)
        {
            findModule.SelectedItem = AllModules;
        }
    }

    // ---- find ---------------------------------------------------------------------------

    private void OnFind(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        // a pasted recipe "signature | offset | path" fills all three fields
        string text = findSignature.Text ?? "";
        if (text.Contains('|'))
        {
            string[] parts = text.Split('|');
            findSignature.Text = text = parts[0].Trim();
            findOffset.Text = parts.Length > 1 ? parts[1].Trim() : "0";
            findPath.Text = parts.Length > 2 ? parts[2].Trim() : "";
        }
        Signature sig;
        long targetOffset;
        try
        {
            sig = Signature.Parse(text);
            targetOffset = ParseOffset(findOffset.Text);
        }
        catch (FormatException ex)
        {
            MessageDialog.ShowMessage(ex.Message, AlertType.WARNING);
            return;
        }
        if (sig.Length < 4 || sig.WildcardCount == sig.Length)
        {
            MessageDialog.ShowMessage("The signature needs at least 4 bytes and some exact (non-??) bytes.", AlertType.WARNING);
            return;
        }
        string path = (findPath.Text ?? "").Trim();
        string moduleName = findModule.SelectedItem as string ?? "main";
        Run(findButton, "Scanning " + moduleName + " for the signature...", debugger =>
        {
            MemoryInfo[] infos = debugger.Query(0, 10000);
            List<MemoryRegions.Module> modules = MemoryRegions.Modules(infos);
            MemoryRegions.Ranges ranges = MemoryRegions.Detect(infos);
            var scan = modules.Where(m => moduleName == AllModules || m.Name == moduleName).ToList();
            if (scan.Count == 0)
            {
                return () => MessageDialog.ShowMessage("Module \"" + moduleName + "\" not found. Attach to a game first.", AlertType.WARNING);
            }
            var rows = new List<Row>();
            int total = 0;
            foreach (MemoryRegions.Module module in scan)
            {
                byte[] code = ReadCode(debugger, module);
                List<int> matches = sig.Scan(code, 4, MaxListedMatches + 1);
                foreach (int off in matches.Take(Math.Max(0, MaxListedMatches - total)))
                {
                    long at = module.Start + off;
                    long target = at + targetOffset;
                    // only what the matched code computes itself (an adrp before the match is unrelated code)
                    List<SignatureMaker.Reference> refs = SignatureMaker.References(code, off, at, sig.Length / 4, lookBack: false);
                    rows.Add(new Row($"#{total + 1}  {Rel(modules, target)}  ({HexUtils.FormatAddress(target)})",
                        target, Rel(modules, target)));
                    if (path.Length > 0)
                    {
                        rows.Add(ResolveRow(debugger, modules, ranges, path, target, refs));
                    }
                    foreach (SignatureMaker.Reference r in refs)
                    {
                        rows.Add(ReferenceRow(r, modules, "      "));
                    }
                    total++;
                }
                total += Math.Max(0, matches.Count - MaxListedMatches);
            }
            int count = total;
            return () =>
            {
                results.Clear();
                foreach (Row r in rows)
                {
                    results.Add(r);
                }
                findInfo.Text = count switch
                {
                    0 => "No match. The code may have changed more than the wildcards cover.",
                    1 when path.Length > 0 => "1 match (unique). The = line is your address; right-click it for the Watch List.",
                    1 => "1 match (unique). Lines with → are the global addresses this code uses.",
                    > MaxListedMatches => $"More than {MaxListedMatches} matches; the signature is far too short.",
                    _ => $"{count} matches; the signature is not unique.",
                };
                mc.SetStatus($"Signature scan: {count} match(es)");
            };
        });
    }

    /// <summary>The final address of one match: ref = what the target instruction computes, then the path.</summary>
    private static Row ResolveRow(Debugger debugger, List<MemoryRegions.Module> modules, MemoryRegions.Ranges ranges,
        string path, long target, List<SignatureMaker.Reference> refs)
    {
        SignatureMaker.Reference? r = refs.FirstOrDefault(x => x.InstructionAddress == target);
        if (r == null)
        {
            return new Row("      = ? (the instruction at the target offset computes no address inside the signature;"
                + " recreate the signature so it includes its adrp)");
        }
        // the same path in pointer syntax for this build, e.g. [main+4774450]+10
        string expr = System.Text.RegularExpressions.Regex.Replace(path, @"\bref\b", ExprName(modules, r.Target));
        long? value = TryEval(debugger, path, r.Target, modules);
        return value is { } v
            ? new Row($"      = {HexUtils.FormatAddress(v)}  {Describe(modules, ranges, v)}  ({expr})", v, expr)
            : new Row($"      = ? ({expr} could not be read)", null, expr);
    }

    private static long ParseOffset(string? text)
    {
        string s = (text ?? "").Trim();
        bool negative = s.StartsWith('-');
        s = s.TrimStart('-', '+');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        if (s.Length == 0)
        {
            return 0;
        }
        if (!long.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long v))
        {
            throw new FormatException("Target offset must be a hex number, e.g. 8 or -0x10");
        }
        return negative ? -v : v;
    }

    // ---- shared -------------------------------------------------------------------------

    /// <summary>Runs <paramref name="work"/> off the UI thread; the returned action updates the UI.</summary>
    private void Run(Button button, string status, Func<Debugger, Action> work)
    {
        Debugger debugger = mc.GetDebugger();
        createButton.IsEnabled = false;
        findButton.IsEnabled = false;
        mc.SetStatus(status);
        Task.Run(() =>
        {
            Action done;
            try
            {
                done = work(debugger);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "signature");
                done = () =>
                {
                    mc.SetStatus("Signature: " + ex.Message);
                    MessageDialog.ShowMessage("Signature failed:\n" + ex.Message, AlertType.ERROR);
                };
            }
            Dispatcher.UIThread.Post(() =>
            {
                createButton.IsEnabled = true;
                findButton.IsEnabled = true;
                mc.SetProgress(0);
                mc.Guard(done);
            });
        });
    }

    /// <summary>A module's code; cached, so Create followed by Find reads it once.</summary>
    private byte[] ReadCode(Debugger debugger, MemoryRegions.Module module)
    {
        long pid = debugger.GetAttachedPid();
        if (cache is { } c && c.Pid == pid && c.Start == module.Start && c.Code.Length == module.TextEnd - module.Start)
        {
            return c.Code;
        }
        byte[] code = ReadRange(debugger, module.Start, module.TextEnd - module.Start);
        cache = new CodeCache(pid, module.Start, code);
        return code;
    }

    /// <summary>A module's data (.rodata/.data/.bss) read region by region; unreadable parts stay zero.</summary>
    private byte[] ReadModuleData(Debugger debugger, MemoryInfo[] infos, MemoryRegions.Module module)
    {
        var data = new byte[module.End - module.TextEnd];
        foreach (MemoryInfo info in infos.Where(i => i.Address >= module.TextEnd && i.NextAddress <= module.End))
        {
            if ((info.Perm & 1) == 0 || info.Size <= 0)
            {
                continue;
            }
            try
            {
                byte[] part = ReadRange(debugger, info.Address, info.Size);
                Array.Copy(part, 0, data, info.Address - module.TextEnd, part.Length);
            }
            catch (ConnectionException ex) when (debugger.Connected)
            {
                Log.Info($"signature: skipping unreadable region {HexUtils.FormatAddress(info.Address)}: {ex.Message}");
            }
        }
        return data;
    }

    /// <summary>Reads memory in 1 MiB pieces so other tabs aren't blocked for long.</summary>
    private byte[] ReadRange(Debugger debugger, long start, long size)
    {
        if (size <= 0 || size > int.MaxValue)
        {
            throw new InvalidOperationException("unexpected code size " + size);
        }
        var code = new byte[size];
        const int piece = 0x100000;
        var buffer = new byte[piece];
        for (long off = 0; off < size; off += piece)
        {
            int n = (int)Math.Min(piece, size - off);
            byte[] b = n == piece ? buffer : new byte[n];
            debugger.Readmem(start + off, n, b);
            Array.Copy(b, 0, code, off, n);
            double p = (double)(off + n) / size;
            Dispatcher.UIThread.Post(() => mc.SetProgress(p));
        }
        return code;
    }

    /// <summary>Disassembly of the signature with wildcards shown, plus the addresses it references.</summary>
    private static List<Row> DescribeCode(byte[] code, MemoryRegions.Module module, List<MemoryRegions.Module> modules,
        long start, int count, long target, Signature sig)
    {
        int off = (int)(start - module.Start);
        var text = new Dictionary<long, string>();
        try
        {
            foreach (CsToolWrapper.Insn i in CsToolWrapper.Disassemble(code.AsSpan(off, count * 4).ToArray(), start))
            {
                text[i.Addr] = i.OpStr;
            }
        }
        catch (Exception ex)
        {
            Log.Info("signature disassembly unavailable: " + ex.Message);
        }

        var rows = new List<Row>();
        for (int k = 0; k < count; k++)
        {
            long at = start + k * 4;
            string bytes = string.Join(' ', Enumerable.Range(k * 4, 4)
                .Select(b => sig.Exact[b] ? sig.Values[b].ToString("X2") : "??"));
            string marker = at == target ? "►" : " ";
            rows.Add(new Row($"{marker} {Rel(modules, at),-16} {bytes}  {text.GetValueOrDefault(at, "")}", at, Rel(modules, at)));
        }
        foreach (SignatureMaker.Reference r in SignatureMaker.References(code, off, start, count))
        {
            rows.Add(ReferenceRow(r, modules, "  "));
        }
        return rows;
    }

    private static Row ReferenceRow(SignatureMaker.Reference r, List<MemoryRegions.Module> modules, string indent)
    {
        string name = Rel(modules, r.Target);
        return new Row($"{indent}→ {r.Kind,-12} {name}  (by {Rel(modules, r.InstructionAddress)})", r.Target, name);
    }

    /// <summary>"main+0x1234" for addresses inside a module, else the plain address.</summary>
    private static string Rel(List<MemoryRegions.Module> modules, long address)
    {
        MemoryRegions.Module? m = modules.FirstOrDefault(x => x.Contains(address));
        return m == null ? HexUtils.FormatAddress(address) : $"{m.Name}+0x{address - m.Start:X}";
    }

    private ContextMenu RowMenu(ListBox list)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action<Row> action)
        {
            var item = new MenuItem { Header = new TextBlock { Text = header } };
            AutomationProperties.SetName(item, header);
            item.Click += (_, _) =>
            {
                if (list.SelectedItem is Row row)
                {
                    mc.Guard(() => action(row));
                }
            };
            return item;
        }
        menu.Items.Add(Item("Copy", r => _ = Copy(r.Expression ?? r.Text.Trim())));
        menu.Items.Add(Item("Copy Address", r => _ = Copy(r.Address is { } a ? HexUtils.FormatAddress(a) : null)));
        menu.Items.Add(Item("Disassembler", Disassemble));
        menu.Items.Add(Item("Memory Viewer", r =>
        {
            if (r.Address is { } a)
            {
                mc.Memory().SetViewAddress(a);
                mc.SetTab(MainWindow.Tab.MEMORY_VIEWER);
            }
        }));
        menu.Items.Add(Item("Add to Watch List", r =>
        {
            if (r.Address is { } a)
            {
                mc.Watch().AddExpression(r.Expression ?? HexUtils.FormatAddress(a));
                mc.SetTab(MainWindow.Tab.WATCH_LIST);
            }
        }));
        menu.Items.Add(Item("Create Signature here", r =>
        {
            if (r.Address is { } a)
            {
                CreateAt(a);
            }
        }));
        return menu;
    }

    private void Disassemble(Row? row)
    {
        if (row?.Address is { } a)
        {
            mc.Disassembly().SetDisassembleAddress(a);
            mc.SetTab(MainWindow.Tab.DISASSEMBLER);
        }
    }

    private async Task Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                mc.SetStatus("Copied " + (text.Length > 60 ? text[..60] + "..." : text));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "clipboard");
        }
    }
}
