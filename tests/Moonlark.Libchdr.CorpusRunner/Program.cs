using Moonlark.Libchdr;
using Moonlark.Libchdr.CorpusRunner;

// Exercises possibly malformed CHDs end to end, out of the test host, for MalformedCorpusTests. For each file it prints
// one tab-separated line: the file name, then "rejected <error>" or "read <summary>" when every check passes, otherwise
// the failure: "overrun", "nondeterministic", "state-dependent", "divergent", "order-dependent", "untyped" or "escaped".
// - Every hunk decodes twice, after 0x00 and 0xFF prefills, each read judged on its own, with a canary after the
//   destination, so a result or bytes that depend on the destination's prior contents, or an overrun, are caught.
// - ReadAt, ChdStream and CD frames must return ReadHunk's bytes, fail where it fails, and leave the hunk cache intact
//   after a failure; metadata payloads, CD sectors and integrity verification must succeed or fail with ChdException.
// - The decode repeats with a 1 MiB and a one-hunk read-ahead window, and in reverse order on a fresh handle, and every
//   hunk must keep its outcome. Every pass runs on a 1 MiB stack, and an input with more than 8,192 hunks is sampled.
// The last line reports the peak memory. The harness detects a crash or hang from the process itself.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Moonlark.Libchdr.CorpusRunner <native-library> <file.chd>...");
    return 2;
}
LibchdrLibrary.Load(Path.GetFullPath(args[0]));
int failures = 0;
foreach (string path in args[1..])
{
    string result;
    try
    { result = CorpusChecks.Exercise(path); }
    catch (CorpusFailure failure) { result = failure.Message; }
    catch (Exception error) { result = $"{(error is ChdException ? "escaped" : "untyped")} {error.GetType().FullName}: {error.Message}"; }
    if (!result.StartsWith("rejected ", StringComparison.Ordinal) && !result.StartsWith("read ", StringComparison.Ordinal))
        failures++;
    Console.WriteLine($"{Path.GetFileName(path)}\t{result}");
    Console.Out.Flush();
}
Console.WriteLine($"{CorpusChecks.PeakWorkingSetLine}\t{ResourceUsage.PeakWorkingSet()}");
return failures == 0 ? 0 : 3;
