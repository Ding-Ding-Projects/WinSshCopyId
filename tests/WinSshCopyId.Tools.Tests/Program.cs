using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinSshCopyId.Tools.Servers;
using WinSshCopyId.Tools.Conversion;
using WinSshCopyId.Tools.Ollama;

var completed = 0;
void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); completed++; }
async Task Reject(Func<Task> action, string detail)
{
    try { await action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException)
    { completed++; return; }
    throw new Exception(detail);
}
var root = Path.Combine(Path.GetTempPath(), "WinSshCopyId-tools-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var text = "# preserved\r\nserver-port=25565\r\ncustom-unknown=value\r\n";
    var settings = ServerProperties.Parse(text).SetPort(25566);
    Check(settings.ToString() == text.Replace("25565", "25566"), "Unknown property or newline lost.");
    await Reject(() => Task.FromResult(settings.Set("x", "a\nb")), "Property injection accepted.");
    var source = Path.Combine(root, "instance"); Directory.CreateDirectory(source);
    await File.WriteAllTextAsync(Path.Combine(source, "world.dat"), "original world");
    var instance = new ServerInstance(Guid.NewGuid(), "test-server", new(ServerTargetKind.LocalProcess, source),
        new("java", ServerEdition.Java, "test", "vanilla", ServerCatalogue.JavaManifest, null, null, null, null, null),
        true, false, 25565, 1024, DateTimeOffset.UtcNow);
    var registry = new ServerRegistry(Path.Combine(root, "state", "instances.json"));
    await registry.SaveAsync(instance);
    Check((await registry.ReadAsync()).Single() == instance, "Registry round trip changed instance.");
    var plan = ServerLifecyclePlanner.Create(instance, ServerOperation.Start, "docker", "ssh");
    Check(!plan.CanExecute && plan.Blockers.Any(x => x.Contains("EULA")), "Unapproved launch became executable.");
    var remote = instance with { Target = instance.Target with { Kind = ServerTargetKind.SshProcess, Host = "example.invalid", User = "test" } };
    Check(!ServerLifecyclePlanner.Create(remote, ServerOperation.Start, "docker", "ssh").CanExecute, "Untrusted SSH launch accepted.");
    var stopped = new ServerObservation(instance.Id, ServerObservedState.Stopped, DateTimeOffset.UtcNow, null, "Test observation");
    var backups = new ServerBackupService();
    var backup = Path.Combine(root, "snapshot");
    var receipt = await backups.CreateAsync(instance, stopped, backup);
    Check(receipt.Files.Length == 1 && (await backups.VerifyAsync(backup)).TotalBytes == 14, "Backup receipt invalid.");
    await File.WriteAllTextAsync(Path.Combine(source, "world.dat"), "modified world");
    var rollback = await backups.RestoreAsync(instance, stopped, backup);
    Check(await File.ReadAllTextAsync(Path.Combine(source, "world.dat")) == "original world", "Restore did not restore bytes.");
    Check(await File.ReadAllTextAsync(Path.Combine(rollback, "world.dat")) == "modified world", "Rollback did not preserve prior world.");
    await File.WriteAllTextAsync(Path.Combine(backup, "world.dat"), "corrupt");
    await Reject(() => backups.VerifyAsync(backup), "Corrupt backup accepted.");
    await Reject(() => backups.CreateAsync(instance, stopped with { State = ServerObservedState.Running }, Path.Combine(root, "bad")), "Running server snapshot accepted.");
    var metadata = "{\"downloads\":{\"server\":{\"url\":\"https://piston-data.mojang.com/server.jar\",\"sha1\":\"" + new string('a',40) + "\",\"size\":123}},\"javaVersion\":{\"majorVersion\":21}}";
    using var client = new HttpClient(new FakeHandler(metadata));
    var catalog = new ServerCatalogue(client);
    var version = new ServerVersion("test", "release", new("https://piston-meta.mojang.com/test.json"), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(metadata))), DateTimeOffset.UtcNow);
    Check((await catalog.ResolveJavaAsync(version))?.JavaMajorVersion == 21, "Source-backed runtime not parsed.");
    await Reject(() => catalog.ResolveJavaAsync(version with { MetadataSha1 = new string('0', 40) }), "Wrong metadata digest accepted.");
    Check(ConversionCatalog.Categories.Count == 8 && ConversionCatalog.Adapters.All(x => !x.Available), "Unproven conversion engine enabled.");
    var converter = new LocalConverter();
    var jsonPath = Path.Combine(root, "input.json");
    await File.WriteAllTextAsync(jsonPath, "{\"value\":42}");
    Check((await converter.PreviewAsync(jsonPath)).DetectedType == "json", "JSON signature was not inspected.");
    await Reject(() => converter.ConvertAsync(jsonPath, Path.Combine(root, "blocked.json"), "json-format"), "Unproven conversion was executed.");
    var outputPath = Path.Combine(root, "formatted.json");
    await converter.VerifyManagedImplementationAsync(jsonPath, outputPath, "json-format");
    using (var output = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(outputPath)))
        Check(output.RootElement.GetProperty("value").GetInt32() == 42, "Managed verification lost JSON values.");
    await Reject(() => converter.VerifyManagedImplementationAsync(jsonPath, outputPath, "json-format"), "Existing destination was overwritten.");
    var queue = new ConversionQueue(Path.Combine(root, "queue"));
    for (var i = 0; i < 205; i++) await queue.EnqueueAsync(jsonPath, Path.Combine(root, "out-" + i), "json-format");
    Check(queue.ReadPage(0, 200).Count() == 200 && queue.ReadPage(200, 200).Count() == 5, "Queue pagination lost records.");
    var ollamaStore = new OllamaStateStore(Path.Combine(root, "ollama"));
    var chat = new OllamaConversation(Guid.NewGuid(), "Local test", "test:latest", DateTimeOffset.UtcNow, [new("user", "private content")]);
    await ollamaStore.SaveConversationAsync(chat);
    Check((await ollamaStore.LoadAsync()).Conversations.Single().Messages.Single().Content == "private content", "Local chat was not persisted.");
    Check(!OllamaStateStore.ExportRedacted(chat).Contains("private content", StringComparison.Ordinal), "Export exposed local model content.");
    Check(OllamaHardwareFit.Unknown.Verdict == "Unknown" && OllamaCatalogSnapshot.Unavailable.Completeness == OllamaCatalogCompleteness.Unavailable, "Missing hardware or catalog evidence became a positive claim.");
    await Reject(() => ollamaStore.DeleteConversationAsync(chat.Id, false), "Unconfirmed local history deletion accepted.");
    Console.WriteLine($"PASS: {completed} local tool assertions; no server process or service launched.");
}
finally { Directory.Delete(root, true); }

sealed class FakeHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request });
}
