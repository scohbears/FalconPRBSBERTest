using System.Diagnostics;

namespace FalconPRBSBERTest;

internal static class Program
{
    const int FrameBits = 256;
    const int Lanes = 8;

    readonly record struct PrbsPoly(string Name, int[] Taps)
    {
        public int MaxTap
        {
            get
            {
                int m = 0;
                foreach (var t in Taps) if (t > m) m = t;
                return m;
            }
        }
        public override string ToString() => Name;
    }

    // Standard PRBS polynomials. Recurrence: b[n] = XOR of b[n - tap] for each tap.
    static readonly PrbsPoly[] StandardPolys = new[]
    {
        new PrbsPoly("PRBS5",  new[] { 3, 5 }),
        new PrbsPoly("PRBS6",  new[] { 5, 6 }),
        new PrbsPoly("PRBS7",  new[] { 6, 7 }),
        new PrbsPoly("PRBS8",  new[] { 4, 5, 6, 8 }),
        new PrbsPoly("PRBS9",  new[] { 5, 9 }),
        new PrbsPoly("PRBS10", new[] { 7, 10 }),
        new PrbsPoly("PRBS11", new[] { 9, 11 }),
        new PrbsPoly("PRBS12", new[] { 6, 8, 11, 12 }),
        new PrbsPoly("PRBS13", new[] { 8, 11, 12, 13 }),
        new PrbsPoly("PRBS14", new[] { 9, 11, 13, 14 }),
        new PrbsPoly("PRBS15", new[] { 14, 15 }),
        new PrbsPoly("PRBS16", new[] { 4, 13, 15, 16 }),
        new PrbsPoly("PRBS17", new[] { 14, 17 }),
        new PrbsPoly("PRBS18", new[] { 11, 18 }),
        new PrbsPoly("PRBS19", new[] { 14, 17, 18, 19 }),
        new PrbsPoly("PRBS20", new[] { 3, 20 }),
        new PrbsPoly("PRBS21", new[] { 19, 21 }),
        new PrbsPoly("PRBS22", new[] { 21, 22 }),
        new PrbsPoly("PRBS23", new[] { 18, 23 }),
        new PrbsPoly("PRBS25", new[] { 22, 25 }),
        new PrbsPoly("PRBS28", new[] { 25, 28 }),
        new PrbsPoly("PRBS29", new[] { 27, 29 }),
        new PrbsPoly("PRBS30", new[] { 7, 28, 29, 30 }),
        new PrbsPoly("PRBS31", new[] { 28, 31 }),
    };

    enum ChunkOrder { Lsb0First, Msb0First }

    sealed class Options
    {
        public string Path = @"D:\bySean\test.vcd";
        public ChunkOrder Order = ChunkOrder.Lsb0First;
        public bool Search = false;
        public int SearchMaxOrder = 64;
        public string? ForcePrbs = null;
        public bool Verbose = false;
    }

    static int Main(string[] args)
    {
        var opt = ParseArgs(args);
        if (opt == null) return 2;

        if (!File.Exists(opt.Path))
        {
            Console.Error.WriteLine($"File not found: {opt.Path}");
            return 1;
        }

        Console.WriteLine($"VCD file: {opt.Path}");
        var sw = Stopwatch.StartNew();
        var (frames, doutId, width) = ParseVcd(opt.Path);
        sw.Stop();
        Console.WriteLine($"Parsed {frames.Count:N0} frames (signal id='{doutId}', width={width}) in {sw.ElapsedMilliseconds} ms");

        if (frames.Count == 0)
        {
            Console.Error.WriteLine("No frames found in VCD.");
            return 1;
        }
        if (width != FrameBits)
        {
            Console.WriteLine($"WARNING: expected {FrameBits}-bit signal, got {width}. Continuing with width={width}.");
        }
        if (width % Lanes != 0)
        {
            Console.Error.WriteLine($"Signal width {width} is not divisible by {Lanes} lanes.");
            return 1;
        }

        if (opt.Verbose)
        {
            DumpFirstFrame(frames[0], width);
        }

        Console.WriteLine();
        Console.WriteLine($"Building per-lane bit streams (chunk order: {opt.Order})...");
        sw.Restart();
        var laneStreams = BuildLaneStreams(frames, width, opt.Order);
        sw.Stop();
        int bitsPerLane = laneStreams[0].Length;
        Console.WriteLine($"  {Lanes} lanes, {bitsPerLane:N0} bits per lane (built in {sw.ElapsedMilliseconds} ms)");

        if (opt.Verbose)
        {
            DumpLaneSample(laneStreams[0]);
            DumpRunHist(laneStreams[0]);
        }

        // Detect polynomial
        var stream0 = laneStreams[0];
        PrbsPoly? lockedPoly = null;
        bool reversed = false;

        if (opt.ForcePrbs != null)
        {
            var forced = StandardPolys.FirstOrDefault(p => string.Equals(p.Name, opt.ForcePrbs, StringComparison.OrdinalIgnoreCase));
            if (forced.Name == null)
            {
                Console.Error.WriteLine($"Unknown PRBS name: {opt.ForcePrbs}. Try one of: {string.Join(", ", StandardPolys.Select(p => p.Name))}");
                return 1;
            }
            lockedPoly = forced;
            Console.WriteLine();
            Console.WriteLine($"Using forced polynomial: {forced.Name}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Detecting PRBS polynomial (using lane 0)...");
            sw.Restart();
            var (best, allResults) = DetectPolynomial(stream0, opt);
            sw.Stop();
            Console.WriteLine($"  detection took {sw.ElapsedMilliseconds} ms over {allResults.Count} candidates");
            Console.WriteLine();
            Console.WriteLine($"  {"Polynomial",-12} {"Time order",-11} {"Mismatches",13} {"Checks",13} {"Rate",13}");
            Console.WriteLine("  " + new string('-', 66));
            foreach (var r in allResults.Take(15))
            {
                Console.WriteLine($"  {r.Poly.Name,-12} {r.Order,-11} {r.Mismatches,13:N0} {r.Checks,13:N0} {r.Rate,13:E3}");
            }

            const double LockThreshold = 0.10;
            if (best.Rate > LockThreshold)
            {
                Console.WriteLine();
                Console.WriteLine($"NO PRBS LOCK (best mismatch rate {best.Rate:E3} > {LockThreshold:E1}).");
                Console.WriteLine("Diagnostic info:");
                DumpFirstFrame(frames[0], width);
                DumpLaneSample(stream0);
                DumpRunHist(stream0);
                Console.WriteLine();
                Console.WriteLine("Things to try:");
                Console.WriteLine($"  - Add --search to scan trinomials up to order {opt.SearchMaxOrder}");
                Console.WriteLine("  - Add --chunk-order msb0first if the byte at frame[255:248] is chunk 0");
                Console.WriteLine("  - Force a specific polynomial with --prbs PRBSn");
                Console.WriteLine("  - Add --verbose to dump frame/lane structure");
                return 1;
            }
            lockedPoly = best.Poly;
            reversed = best.Order == "reverse";
            Console.WriteLine();
            Console.WriteLine($"Locked: {best.Poly.Name} ({best.Order} time order across the stream)");
        }

        if (reversed)
        {
            for (int k = 0; k < Lanes; k++) Array.Reverse(laneStreams[k]);
        }

        var poly = lockedPoly.Value;
        int divisor = poly.Taps.Length + 1; // # of (mis)match terms per real bit error

        Console.WriteLine();
        Console.WriteLine($"BER per lane (polynomial {poly.Name}):");
        Console.WriteLine($"  Self-check recurrence: each true bit error -> {divisor} mismatches");
        Console.WriteLine($"  Est. BER = Mismatches / ({divisor} * Checks)");
        Console.WriteLine();
        Console.WriteLine($"  {"Lane",-6} {"Mismatches",13} {"Checks",13} {"Raw rate",13} {"Est. BER",13}");
        Console.WriteLine("  " + new string('-', 62));
        long totalMis = 0, totalChk = 0;
        for (int k = 0; k < Lanes; k++)
        {
            long m = CountMismatches(laneStreams[k], poly);
            long c = laneStreams[k].Length - poly.MaxTap;
            double raw = (double)m / c;
            double ber = raw / divisor;
            Console.WriteLine($"  {k,-6} {m,13:N0} {c,13:N0} {raw,13:E3} {ber,13:E3}");
            totalMis += m;
            totalChk += c;
        }
        Console.WriteLine("  " + new string('-', 62));
        double totalRaw = (double)totalMis / totalChk;
        Console.WriteLine($"  {"ALL",-6} {totalMis,13:N0} {totalChk,13:N0} {totalRaw,13:E3} {totalRaw / divisor,13:E3}");
        return 0;
    }

    static Options? ParseArgs(string[] args)
    {
        var opt = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                case "--search":
                    opt.Search = true;
                    break;
                case "--search-max":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out opt.SearchMaxOrder))
                    { Console.Error.WriteLine("--search-max needs an integer"); return null; }
                    break;
                case "--prbs":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--prbs needs a name (e.g. PRBS31)"); return null; }
                    opt.ForcePrbs = args[++i];
                    break;
                case "--chunk-order":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--chunk-order needs lsb0first|msb0first"); return null; }
                    var v = args[++i].ToLowerInvariant();
                    opt.Order = v switch
                    {
                        "lsb0first" or "lsb" => ChunkOrder.Lsb0First,
                        "msb0first" or "msb" => ChunkOrder.Msb0First,
                        _ => throw new ArgumentException()
                    };
                    break;
                case "-v":
                case "--verbose":
                    opt.Verbose = true;
                    break;
                default:
                    if (a.StartsWith("-")) { Console.Error.WriteLine($"Unknown option: {a}"); return null; }
                    opt.Path = a;
                    break;
            }
        }
        return opt;
    }

    static void PrintHelp()
    {
        Console.WriteLine("Usage: FalconPRBSBERTest [<vcd-path>] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --prbs <name>          Force a specific polynomial (PRBS7, PRBS15, PRBS31, ...)");
        Console.WriteLine("  --chunk-order <ord>    lsb0first (default) or msb0first within each frame");
        Console.WriteLine("  --search               Also do exhaustive trinomial search up to order 64");
        Console.WriteLine("  --search-max <n>       Max order for exhaustive search (default 64)");
        Console.WriteLine("  -v, --verbose          Dump frame/lane diagnostics");
        Console.WriteLine("  -h, --help             This help");
    }

    record struct DetectResult(PrbsPoly Poly, string Order, long Mismatches, long Checks, double Rate);

    static (DetectResult Best, List<DetectResult> All) DetectPolynomial(byte[] stream0, Options opt)
    {
        var rev = (byte[])stream0.Clone();
        Array.Reverse(rev);
        var all = new List<DetectResult>();

        foreach (var poly in StandardPolys)
        {
            long checks = stream0.Length - poly.MaxTap;
            if (checks <= 0) continue;
            long mF = CountMismatches(stream0, poly);
            long mR = CountMismatches(rev, poly);
            all.Add(new DetectResult(poly, "forward", mF, checks, (double)mF / checks));
            all.Add(new DetectResult(poly, "reverse", mR, checks, (double)mR / checks));
        }

        if (opt.Search)
        {
            // Exhaustive trinomial search: b[n] = b[n-a] XOR b[n-b] for 1 <= a < b <= maxOrder
            for (int b = 3; b <= opt.SearchMaxOrder; b++)
            {
                if (stream0.Length - b <= 0) break;
                for (int a = 1; a < b; a++)
                {
                    var p = new PrbsPoly($"x^{b}+x^{a}+1", new[] { a, b });
                    long checks = stream0.Length - b;
                    long mF = CountMismatches(stream0, p);
                    if ((double)mF / checks < 0.40)
                        all.Add(new DetectResult(p, "forward", mF, checks, (double)mF / checks));
                    long mR = CountMismatches(rev, p);
                    if ((double)mR / checks < 0.40)
                        all.Add(new DetectResult(p, "reverse", mR, checks, (double)mR / checks));
                }
            }
        }

        all.Sort((x, y) => x.Rate.CompareTo(y.Rate));
        return (all[0], all);
    }

    static (List<string> Frames, string DoutId, int Width) ParseVcd(string path)
    {
        string doutId = "";
        int width = 0;
        var frames = new List<string>();

        using var reader = new StreamReader(path);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            string t = line.Trim();
            if (t.StartsWith("$var"))
            {
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[2], out int w) && w > width)
                {
                    width = w;
                    doutId = parts[3];
                }
            }
            if (t == "$enddefinitions" || t.StartsWith("$enddefinitions ")) break;
        }
        if (width == 0) return (frames, doutId, width);

        string currentDout = new string('0', width);
        bool haveDout = false;
        bool sawTime = false;

        while ((line = reader.ReadLine()) != null)
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            char c0 = t[0];
            if (c0 == '$') continue;
            if (c0 == '#')
            {
                if (sawTime && haveDout) frames.Add(currentDout);
                sawTime = true;
                continue;
            }
            if (c0 == 'b' || c0 == 'B')
            {
                int sp = t.IndexOf(' ');
                if (sp <= 1) continue;
                string bits = t.Substring(1, sp - 1);
                string id = t.Substring(sp + 1).TrimEnd();
                if (id == doutId)
                {
                    currentDout = PadVcd(bits, width);
                    haveDout = true;
                }
            }
        }
        if (sawTime && haveDout) frames.Add(currentDout);
        return (frames, doutId, width);
    }

    static string PadVcd(string value, int width)
    {
        if (value.Length == width) return value;
        if (value.Length > width) return value.Substring(value.Length - width);
        return new string('0', width - value.Length) + value;
    }

    static byte[][] BuildLaneStreams(List<string> frames, int width, ChunkOrder order)
    {
        int chunksPerFrame = width / Lanes;
        int total = frames.Count * chunksPerFrame;
        var streams = new byte[Lanes][];
        for (int k = 0; k < Lanes; k++) streams[k] = new byte[total];

        for (int f = 0; f < frames.Count; f++)
        {
            string frame = frames[f];
            int baseIdx = f * chunksPerFrame;
            for (int c = 0; c < chunksPerFrame; c++)
            {
                // In the source frame, "chunk index 0" = LSB-side 8 bits = frame[width-8..width-1]
                // chunk index c (LSB-side) -> frame bits [c*8 .. c*8+7]
                int sourceChunk = order == ChunkOrder.Lsb0First ? c : (chunksPerFrame - 1 - c);
                int chunkBase = sourceChunk * Lanes;
                for (int k = 0; k < Lanes; k++)
                {
                    int charIdx = width - 1 - (chunkBase + k);
                    streams[k][baseIdx + c] = (byte)(frame[charIdx] == '1' ? 1 : 0);
                }
            }
        }
        return streams;
    }

    static long CountMismatches(byte[] stream, PrbsPoly poly)
    {
        long mis = 0;
        int start = poly.MaxTap;
        int len = stream.Length;
        var taps = poly.Taps;
        if (taps.Length == 2)
        {
            int t0 = taps[0], t1 = taps[1];
            for (int n = start; n < len; n++)
            {
                int e = stream[n - t0] ^ stream[n - t1];
                if (stream[n] != e) mis++;
            }
        }
        else
        {
            for (int n = start; n < len; n++)
            {
                int e = 0;
                for (int i = 0; i < taps.Length; i++) e ^= stream[n - taps[i]];
                if (stream[n] != e) mis++;
            }
        }
        return mis;
    }

    static void DumpFirstFrame(string frame, int width)
    {
        Console.WriteLine();
        Console.WriteLine("Frame 0 bytes (chunk 0 = LSB-side):");
        int chunks = width / Lanes;
        for (int c = 0; c < chunks; c++)
        {
            string byteStr = frame.Substring(width - 8 * (c + 1), 8);
            int v = Convert.ToInt32(byteStr, 2);
            Console.Write($" {c:00}:0x{v:X2}");
            if (c % 8 == 7) Console.WriteLine();
        }
    }

    static void DumpLaneSample(byte[] stream)
    {
        int n = Math.Min(128, stream.Length);
        var sb = new System.Text.StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append(stream[i] == 0 ? '0' : '1');
        Console.WriteLine($"Lane 0 first {n} bits: {sb}");
    }

    static void DumpRunHist(byte[] stream)
    {
        var hist = new Dictionary<int, long>();
        if (stream.Length == 0) return;
        byte cur = stream[0];
        int len = 1;
        for (int i = 1; i < stream.Length; i++)
        {
            if (stream[i] == cur) len++;
            else { hist[len] = hist.TryGetValue(len, out var c) ? c + 1 : 1; cur = stream[i]; len = 1; }
        }
        hist[len] = hist.TryGetValue(len, out var c2) ? c2 + 1 : 1;
        int maxKey = 0;
        foreach (var k in hist.Keys) if (k > maxKey) maxKey = k;
        Console.WriteLine("Lane 0 run-length distribution (max run = " + maxKey + "):");
        for (int k = 1; k <= maxKey; k++)
        {
            if (!hist.TryGetValue(k, out var v)) continue;
            Console.WriteLine($"  run={k,3}  count={v:N0}");
        }
    }
}
