using WinSshCopyId.Tools.Conversion;

// This entry point is for bounded implementation verification. A normal process
// provides no network confinement and must never enable production adapters.
if (args.Length != 4 || args[0] != "--verify-managed") return 64;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    var result = await new LocalConverter().VerifyManagedImplementationAsync(args[2], args[3], args[1], timeout.Token);
    Console.Out.WriteLine(result.Status);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Conversion cancelled or timed out."); return 124; }
catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or System.Text.Json.JsonException
    or System.Text.DecoderFallbackException or UnauthorizedAccessException or ArgumentException)
{ Console.Error.WriteLine("Conversion failed: " + ex.GetType().Name); return 1; }
