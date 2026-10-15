using System.Text;
using WinSshCopyId.Workspace.Security;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
void Throws(Action action,string name){try{action();}catch(FormatException){checks++;return;}throw new Exception(name);}
long[] times=[59,1111111109,1111111111,1234567890,2000000000,20000000000];
string[][] vectors=[["94287082","07081804","14050471","89005924","69279037","65353130"],["46119246","68084774","67062674","91819424","90698825","77737706"],["90693936","25091201","99943326","93441116","38618901","47863826"]];
foreach(var algorithm in Enum.GetValues<OtpAlgorithm>()){int length=algorithm==OtpAlgorithm.Sha1?20:algorithm==OtpAlgorithm.Sha256?32:64;var secret=Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890",7))[..length]);for(int i=0;i<times.Length;i++){var code=Totp.Code(secret,DateTimeOffset.FromUnixTimeSeconds(times[i]),algorithm,8);Check(code==vectors[(int)algorithm][i],"RFC6238");Check(Totp.Code(secret,DateTimeOffset.FromUnixTimeSeconds(times[i]),algorithm,6)==code[^6..],"six digits");}}
string[] hotp=["755224","287082","359152","969429","338314","254676","287922","162583","399871","520489"];
for(int i=0;i<10;i++)Check(Totp.Hotp(Encoding.ASCII.GetBytes("12345678901234567890"),i)==hotp[i],"RFC4226");
Check(Encoding.ASCII.GetString(Totp.DecodeBase32("MZXW6==="))=="foo","base32");foreach(var bad in new[]{"A","MZ======","MZXW6=","MZXW6===X","@@@@"})Throws(()=>Totp.DecodeBase32(bad),"invalid base32");
var data=new Dictionary<string,string>();var vault=new VaultDelegates(k=>data.GetValueOrDefault(k),(k,v)=>data[k]=v,k=>data.Remove(k));var now=DateTimeOffset.FromUnixTimeSeconds(1234567890);using var engine=new ToyLockEngine(vault,()=>now);string seed="GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";var otp=Totp.Code(Totp.DecodeBase32(seed),now);
foreach(var policy in Enum.GetValues<LockPolicy>()){var answers=ToyLockEngine.Factors(policy).Select(f=>f==LockFactor.Pin?"1234":f==LockFactor.Password?"synthetic-password":seed).ToArray();var definition=engine.Create(policy.ToString(),policy,answers,TimeSpan.FromSeconds(5));Check(engine.Status(definition.Target).Locked,"launch locked");Check(engine.Authenticate(definition.Target,answers.Select((s,i)=>ToyLockEngine.Factors(policy)[i]==LockFactor.Totp?otp:s).ToArray()),"policy unlock");Check(!engine.Status(definition.Target).Locked,"unlocked");engine.Relock(definition.Target);Check(engine.Status(definition.Target).Locked,"relock");}
engine.Create("a",LockPolicy.Pin,["1234"],TimeSpan.FromSeconds(5),1);engine.Create("b",LockPolicy.Pin,["5678"],TimeSpan.FromSeconds(5),1);Check(engine.Authenticate("a",["1234"]),"isolation a");Check(engine.Status("b").Locked,"isolation b");now+=TimeSpan.FromSeconds(6);Check(engine.Status("a").Locked,"duration");
var ladder=new UnlockLadder(engine,()=>now);
for(int n=0;n<3;n++){Check(!engine.Authenticate("a",["0000"]),"bad credential");var before=engine.Status("a");var challenge=ladder.Next("a")!;var answer=Array.IndexOf(challenge.Choices.ToArray(),challenge.Prompt[23..]).ToString();Check(ladder.Submit("a",challenge.Nonce,answer),"dish clears wait");Check(!ladder.Submit("a",challenge.Nonce,answer),"replay");var after=engine.Status("a");Check(after.Locked&&after.Escalation==before.Escalation&&after.AttemptsRemaining==1,"wait only");}
engine.Authenticate("a",["0000"]);Check(ladder.Next("a")==null,"rolling budget");
engine.Authenticate("b",["0000"]);var school=ladder.Next("b",true)!;Check(school.Stage==LadderStage.Sums,"school starts sums");Check(!ladder.Submit("b",school.Nonce,"wrong"),"wrong sum");var m=ladder.Next("b",true)!;Check(m.Stage==LadderStage.Moles,"moles");var hits=m.Moles.Select(x=>new MoleHit(x.Id,x.Cell,x.StartsMilliseconds)).ToArray();Check(!ladder.Submit("b",m.Nonce,hits:hits),"early mole submission");Check(ladder.Next("b",true)==null,"clock fallback");
engine.Create("c",LockPolicy.Pin,["1234"],TimeSpan.FromSeconds(5),1);engine.Authenticate("c",["0000"]);
for(int i=0;i<5;i++){var dish=ladder.Next("c")!;Check(!ladder.Submit("c",dish.Nonce,"invalid"),"dish failure");}
Check(ladder.Next("c")!.Stage==LadderStage.Sums,"five dishes escalation");
for(int i=0;i<10;i++){var sum=ladder.Next("c")!;var parts=sum.Prompt.Split(" + ");var correct=(int.Parse(parts[0])+int.Parse(parts[1])).ToString();Check(ladder.Submit("c",sum.Nonce,correct)==(i==9),"ten sums");}
Check(engine.Status("c").Locked&&engine.Status("c").WaitingUntil==null,"sums wait only");
engine.Create("d",LockPolicy.Pin,["1234"],TimeSpan.FromSeconds(5),1);engine.Authenticate("d",["0000"]);var sc=ladder.Next("d",true)!;ladder.Submit("d",sc.Nonce,"wrong");var round=ladder.Next("d",true)!;
Check(!ladder.Hit("d",round.Nonce,99,0),"unknown mole");var roundStart=now;
foreach(var mole in round.Moles.Take(6)){now=roundStart+TimeSpan.FromMilliseconds(mole.StartsMilliseconds);Check(ladder.Hit("d",round.Nonce,mole.Id,mole.Cell),"visible mole");Check(!ladder.Hit("d",round.Nonce,mole.Id,mole.Cell),"duplicate mole");}
now=roundStart+TimeSpan.FromSeconds(10);Check(ladder.Submit("d",round.Nonce),"mole win after duration");Check(engine.Status("d").Locked,"mole never authenticates");
engine.Create("e",LockPolicy.Pin,["1234"],TimeSpan.FromSeconds(5),1);engine.Authenticate("e",["0000"]);var expired=ladder.Next("e")!;now+=TimeSpan.FromSeconds(31);Check(!ladder.Submit("e",expired.Nonce,"0"),"expired challenge");
now+=TimeSpan.FromHours(1);Check(engine.Status("a").AttemptsRemaining==1,"clock restores same budget");engine.Authenticate("a",["0000"]);Check(ladder.Next("a")!=null,"rolling budget replenishes");
var definitions=new List<ToyLockDefinition>();using var original=new ToyLockEngine(vault,()=>now);using var originalLadder=new UnlockLadder(original,()=>now);definitions.Add(original.Create("persistent",LockPolicy.Pin,["2468"],TimeSpan.FromSeconds(5),1));
for(int i=0;i<3;i++){original.Authenticate("persistent",["wrong"]);var c=originalLadder.Next("persistent")!;originalLadder.Submit("persistent",c.Nonce,Array.IndexOf(c.Choices.ToArray(),c.Prompt[23..]).ToString());}
original.Authenticate("persistent",["wrong"]);var lockSnapshot=original.ExportBudgets();var ladderSnapshot=originalLadder.ExportBudgets();
using var restarted=new ToyLockEngine(vault,()=>now);foreach(var d in definitions)restarted.Load(d);restarted.ImportBudgets(lockSnapshot);using var restartedLadder=new UnlockLadder(restarted,()=>now);restartedLadder.ImportBudgets(ladderSnapshot);
Check(restarted.Status("persistent")==original.Status("persistent"),"restart lock budgets");Check(restartedLadder.Next("persistent")==null,"restart skip budgets");
var backupTime=now;now-=TimeSpan.FromSeconds(1);try{restarted.ImportBudgets(lockSnapshot);throw new Exception("rollback accepted");}catch(ArgumentException){checks++;}now=backupTime;
var mutable=new Dictionary<LockFactor,string>{{LockFactor.Pin,definitions[0].References[LockFactor.Pin]}};restarted.Load(new("immutable",LockPolicy.Pin,mutable,TimeSpan.FromSeconds(5)));mutable[LockFactor.Pin]="missing";Check(restarted.Authenticate("immutable",["2468"]),"loaded immutable copy");
try{restarted.Load(new("bad",LockPolicy.PinPassword,new Dictionary<LockFactor,string>{{LockFactor.Pin,"same"},{LockFactor.Password,"same"}},TimeSpan.FromSeconds(5)));throw new Exception("duplicate references accepted");}catch(ArgumentException){checks++;}
Check(!restarted.Authenticate("immutable",[new string('x',1025)]),"overlong answer rejected");
restartedLadder.Dispose();try{restartedLadder.Next("persistent");throw new Exception("disposed ladder usable");}catch(ObjectDisposedException){checks++;}
var store=new AuthenticatorStore(vault);var entry=store.Register("Synthetic","Test",seed,otp,DateTimeOffset.FromUnixTimeSeconds(1234567890));Check(store.CurrentCode(entry,DateTimeOffset.FromUnixTimeSeconds(1234567890))==otp,"vault authenticator");store.Remove(entry);Check(!data.ContainsKey(entry.SecretReference),"remove entry");Console.WriteLine($"PASS: {checks} synthetic security assertions; RFC 4226 and RFC 6238 SHA-1/SHA-256/SHA-512 vectors verified.");


