using System.Diagnostics;

namespace FalconPRBSBERTest;

internal static class Program
{
    private const string VcdPath = @"C:\Users\Sean\Desktop\test.vcd";
    private const int SignalWidth = 256;
    private const int SampleBits = 8;
    private const int Ref1 = 3;
    private const int Ref2 = 4;

    private static void Main()
    {
        long[] error = new long[SampleBits];
        long[] total = new long[SampleBits];
        List<long[]> refMismatchSnapshots = new();
        long refMismatchCount = 0;
        long sampleCount = 0;

        foreach (string line in File.ReadLines(VcdPath))
        {
            if (line.Length == 0 || line[0] != 'b') continue;

            int spaceIdx = line.IndexOf(' ');
            string raw = spaceIdx > 0 ? line.Substring(1, spaceIdx - 1) : line.Substring(1);

            // VCD strips leading zeros; restore full 256-bit width.
            string bits = raw.PadLeft(SignalWidth, '0');

            for (int g = 0; g + SampleBits <= bits.Length; g += SampleBits)
            {
                char b3 = bits[g + Ref1];
                char b4 = bits[g + Ref2];

                if (b3 != b4)
                {
                    refMismatchSnapshots.Add((long[])error.Clone());
                    refMismatchCount++;
                    for (int i = 0; i < SampleBits; i++)
                    {
                        error[i]++;
                        total[i]++;
                    }
                }
                else
                {
                    for (int i = 0; i < SampleBits; i++)
                    {
                        if (bits[g + i] != b3) error[i]++;
                        total[i]++;
                    }
                }
                sampleCount++;
            }
        }

        Console.WriteLine($"VCD file              : {VcdPath}");
        Console.WriteLine($"Total 8-bit samples   : {sampleCount}");
        Console.WriteLine($"Ref mismatches (3!=4) : {refMismatchCount}");
        Console.WriteLine();
        Console.WriteLine("Bit | Errors      | Total       | BER");
        Console.WriteLine("----+-------------+-------------+----------------");
        for (int i = 0; i < SampleBits; i++)
        {
            double ber = total[i] == 0 ? 0.0 : (double)error[i] / total[i];
            Console.WriteLine($" {i}  | {error[i],11} | {total[i],11} | {ber:E6}");
        }

        if (refMismatchSnapshots.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Captured {refMismatchSnapshots.Count} error[] snapshots at ref-mismatch events.");
        }
    }
}
