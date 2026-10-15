using System.Text.Json;
using WinSshCopyId.UI;

int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;}

// No file/network service is created or injected: references remain opaque string data.
var hostile="\\\\untrusted.example.invalid\\share\\identity.pub";
var imported=ProfileKeySelection<object>.FromProfile(hostile);
Check(imported.Reference==hostile&&imported.Selected is null,"import preserves opaque UNC reference without selecting it");
var document=JsonSerializer.Serialize(new{KeyPath=imported.Reference});
var roundtrip=JsonSerializer.Deserialize<Dictionary<string,string>>(document)!;
var restarted=ProfileKeySelection<object>.FromProfile(roundtrip["KeyPath"]);
Check(restarted.Reference==hostile&&restarted.Selected is null,"restart cannot turn metadata into selection");
var picked=new object();var chosen=imported.Choose(picked,"explicit-picker-result.pub");
Check(ReferenceEquals(chosen.Selected,picked)&&chosen.Reference=="explicit-picker-result.pub","explicit selection supplies key");
Check(ProfileKeySelection<object>.FromProfile(hostile).Selected is null,"another import clears prior selection");
Check(chosen.ClearSelection().Selected is null&&chosen.ClearSelection().Reference==chosen.Reference,"clear retains only metadata");

using(var cancellation=new CancellationTokenSource())
{
    int reviews=0,deployments=0;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run=RetryPhaseCoordinator.RunAsync(async token=>{started.SetResult();await Task.Delay(Timeout.Infinite,token);},token=>{reviews++;return Task.FromResult(true);},token=>{deployments++;return Task.CompletedTask;},cancellation.Token);
    await started.Task;cancellation.Cancel();
    Check(await run==RetryPhaseResult.Cancelled&&reviews==0&&deployments==0,"cancel during verification prevents later review and deployment");
}
using(var cancellation=new CancellationTokenSource())
{
    int reviews=0,deployments=0;
    var result=await RetryPhaseCoordinator.RunAsync(token=>{cancellation.Cancel();return Task.CompletedTask;},token=>{reviews++;return Task.FromResult(true);},token=>{deployments++;return Task.CompletedTask;},cancellation.Token);
    Check(result==RetryPhaseResult.Cancelled&&reviews==0&&deployments==0,"late cancellation at phase boundary prevents review");
}
using(var cancellation=new CancellationTokenSource())
{
    int deployments=0;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var answer=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var run=RetryPhaseCoordinator.RunAsync(token=>Task.CompletedTask,token=>{started.SetResult();return answer.Task;},token=>{deployments++;return Task.CompletedTask;},cancellation.Token);
    await started.Task;cancellation.Cancel();answer.SetResult(true);
    Check(await run==RetryPhaseResult.Cancelled&&deployments==0,"cancel during review overrides a late Continue answer");
}
using(var cancellation=new CancellationTokenSource())
{
    var completed=new[]{"retained-installed","retained-already-present"};var pending="failed-not-retried";
    var result=await RetryPhaseCoordinator.RunAsync(token=>Task.CompletedTask,token=>Task.FromResult(false),token=>{pending="deployed";return Task.CompletedTask;},cancellation.Token);
    Check(result==RetryPhaseResult.Declined&&pending=="failed-not-retried"&&completed.SequenceEqual(new[]{"retained-installed","retained-already-present"}),"declined review preserves deployment and completed outcomes");
}
using(var cancellation=new CancellationTokenSource())
{
    var order=new List<string>();bool sameToken=true;
    var result=await RetryPhaseCoordinator.RunAsync(token=>{sameToken&=token==cancellation.Token;order.Add("verify");return Task.CompletedTask;},token=>{sameToken&=token==cancellation.Token;order.Add("continue");return Task.FromResult(true);},token=>{sameToken&=token==cancellation.Token;order.Add("deploy");return Task.CompletedTask;},cancellation.Token);
    Check(result==RetryPhaseResult.Completed&&sameToken&&order.SequenceEqual(new[]{"verify","continue","deploy"}),"deployment requires explicit Continue and the same cancellation identity");
}
using(var cancellation=new CancellationTokenSource())
{
    cancellation.Cancel();bool touched=false;
    var result=await RetryPhaseCoordinator.RunAsync(token=>{touched=true;return Task.CompletedTask;},token=>Task.FromResult(true),token=>Task.CompletedTask,cancellation.Token);
    Check(result==RetryPhaseResult.Cancelled&&!touched,"pre-cancelled batch starts no phase");
}
ControlGuidance.Validate(ControlGuidance.All);ControlGuidance.ValidateAttachments(ControlGuidance.RequiredIds);
Check(ControlGuidance.InventoryId=="info-icon-guidance","separate guidance inventory identity");
foreach(var id in ControlGuidance.RequiredIds)
{
    bool rejected=false;try{ControlGuidance.Validate(ControlGuidance.All.Where(x=>x.Id!=id));}catch(InvalidDataException){rejected=true;}Check(rejected,"omitted guidance descriptor: "+id);
    rejected=false;try{ControlGuidance.ValidateAttachments(ControlGuidance.RequiredIds.Where(x=>x!=id));}catch(InvalidDataException){rejected=true;}Check(rejected,"omitted attached info entry: "+id);
}
var portGuide=ControlGuidance.Get("connection.port");
Check(portGuide.Preview("22")=="Valid SSH port: 22"&&portGuide.Preview("65536").StartsWith("Enter"),"port playground validates range");
var original=portGuide.Preview(portGuide.Example);_ = portGuide.Preview("bad");Check(portGuide.Preview(portGuide.Example)==original,"reset example has no shared mutable draft");
Check(ControlGuidance.Get("connection.install").Preview("example.test").EndsWith("No real action was performed."),"consequential playground is an explicit synthetic preview");
Console.WriteLine($"PASS {passed} UI logic behavioral checks. No native UI, filesystem key resolution or network operation was used.");
