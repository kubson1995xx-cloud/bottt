using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;
namespace Apex.Backtester.Naval;

internal static class SharedPortfolioCommand
{
    internal static readonly JsonSerializerOptions Json=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter(null,false)}
    };
    internal static int Run(string[] args)
    {
        try
        {
            var options=new Dictionary<string,string>();
            for(int i=0;i<args.Length;i+=2)
                if(i+1>=args.Length||(args[i]!="--input"&&args[i]!="--state-dir"&&args[i]!="--output")||!options.TryAdd(args[i],args[i+1]))throw new ArgumentException("Unknown/duplicate/missing shared replay option");
            if(options.Count!=3)throw new ArgumentException("Require --input FILE --state-dir DIR --output FILE");
            string input=Local(options["--input"]),state=Local(options["--state-dir"]),output=Local(options["--output"]);
            if(output==input||output==state||output.StartsWith(state+Path.DirectorySeparatorChar,StringComparison.Ordinal)||input.StartsWith(state+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new ArgumentException("Input/output must be separate from authority directory");
            if(new FileInfo(input).Length>10000000)throw new ArgumentException("Input exceeds bounded local replay size");
            byte[] bytes=File.ReadAllBytes(input);
            var manifest=JsonSerializer.Deserialize<SharedReplayInput>(bytes,Json)??throw new ArgumentException("Empty input");
            using var replay=new SharedPortfolioReplay(manifest,state);
            var node=JsonSerializer.SerializeToNode(replay.Run(),Json)!.AsObject();
            node["schema"]="naval-shared-replay-result-v1";node["inputPath"]=input;node["inputSha256"]=Hash(bytes);
            node["coreSha256"]=Hash(File.ReadAllBytes(typeof(NavalPortfolioCoordinator).Assembly.Location));
            node["backtesterSha256"]=Hash(File.ReadAllBytes(typeof(Program).Assembly.Location));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllText(output,node.ToJsonString(Json));
            Console.WriteLine(output);return 0;
        }
        catch(Exception e){Console.Error.WriteLine("OFFLINE shared replay refused: "+e.Message);return 2;}
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Local(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Contains("://")||path.IndexOf('\0')>=0)throw new ArgumentException("Local filesystem path required");
        path=Path.GetFullPath(path);
        for(string? p=path;p!=null;p=Path.GetDirectoryName(p))
        {
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Symlink/reparse paths not supported for offline authority");
        }
        return path;
    }
}
