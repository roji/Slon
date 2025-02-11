using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using PerfLabTests;

namespace Slon.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<Resolver>(DefaultConfig.Instance, args);
    }
}

