
using CommandLine;

namespace Kahuna;

public sealed class KahunaCommandLineOptions
{
    [Option('h', "host", Required = false, HelpText = "Host to bind incoming connections to", Default = "*")]
    public string Host { get; set; } = "localhost";

    [Option('p', "port", Required = false, HelpText = "Port to bind incoming connections to", Default = 2070)]
    public int Port { get; set; }

    [Option("initial-cluster", Required = false, HelpText = "Initial cluster configuration for static discovery", Default = "")]
    public string InitialCluster { get; set; } = "";
    
    [Option("initial-cluster-partitions", Required = false, HelpText = "Initial cluster number of partitions", Default = 8)]
    public int InitialClusterPartitions { get; set; }
}