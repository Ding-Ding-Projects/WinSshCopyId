using System.Security.Cryptography;
using System.Text.Json;
namespace WinSshCopyId.Workspace.Security;
public enum LockPolicy { Pin, Password, PinPassword, PasswordTotp, PinTotp, PasswordPinTotp }
public enum LockFactor { Pin, Password, Totp }
public sealed record ToyLockDefinition(string Target,LockPolicy Policy,IReadOnlyDictionary<LockFactor,string> References,TimeSpan UnlockDuration,int AttemptLimit=3);
public sealed record LockStatus(bool Locked,int AttemptsRemaining,int Escalation,DateTimeOffset? WaitingUntil);
public sealed class ToyLockEngine(VaultDelegates vault,Func<DateTimeOffset>? clock=null):IDisposable
{
    readonly Func<DateTimeOffset> now=clock??(()=>DateTimeOffset.UtcNow);
    readonly Dictionary<string,State> states=new(StringComparer.Ordinal);
    bool disposed;
    sealed class State(ToyLockDefinition definition) { public ToyLockDefinition Definition=definition; public int Attempts=definition.AttemptLimit; public int Escalation; public DateTimeOffset? Waiting,Unlocked; }
    public static IReadOnlyList<LockFactor> Factors(LockPolicy policy)=>policy switch { LockPolicy.Pin=>[LockFactor.Pin],LockPolicy.Password=>[LockFactor.Password],LockPolicy.PinPassword=>[LockFactor.Pin,LockFactor.Password],LockPolicy.PasswordTotp=>[LockFactor.Password,LockFactor.Totp],LockPolicy.PinTotp=>[LockFactor.Pin,LockFactor.Totp],LockPolicy.PasswordPinTotp=>[LockFactor.Password,LockFactor.Pin,LockFactor.Totp],_=>throw new ArgumentOutOfRangeException(nameof(policy)) };
    public ToyLockDefinition Create(string target,LockPolicy policy,IReadOnlyList<string> credentials,TimeSpan duration,int attemptLimit=3)
    {
        ObjectDisposedException.ThrowIf(disposed,this); ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var factors=Factors(policy); if(credentials.Count!=factors.Count || duration<=TimeSpan.Zero || duration>TimeSpan.FromDays(1)||attemptLimit is <1 or >20)throw new ArgumentException("Invalid lock settings.");
        var refs=new Dictionary<LockFactor,string>(); var pending=new Dictionary<string,string>();
        for(int i=0;i<factors.Count;i++) { var factor=factors[i]; string value=credentials[i]; string encoded;
            if(factor==LockFactor.Totp) { var bytes=Totp.DecodeBase32(value); try{encoded=Convert.ToBase64String(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);} }
            else { if(value.Length>1024 || (factor==LockFactor.Pin ? value.Length<4||value.Length>32||value.Any(c=>c<'0'||c>'9') : value.Length<1))throw new ArgumentException("Invalid credential."); var salt=RandomNumberGenerator.GetBytes(16); var hash=Rfc2898DeriveBytes.Pbkdf2(value,salt,210000,HashAlgorithmName.SHA256,32); encoded=JsonSerializer.Serialize(new PasswordHash(Convert.ToBase64String(salt),Convert.ToBase64String(hash))); }
            var reference="toy-lock/"+Guid.NewGuid().ToString("N"); refs[factor]=reference; pending[reference]=encoded;
        }
        var written=new List<string>(); try { foreach(var pair in pending) {vault.Set(pair.Key,pair.Value);written.Add(pair.Key);} } catch {foreach(var reference in written)vault.Delete(reference);throw;}
        if(states.ContainsKey(target))Remove(target);
        var definition=new ToyLockDefinition(target,policy,refs,duration,attemptLimit); states[target]=new(definition); return definition;
    }
    public void Load(ToyLockDefinition definition) { ObjectDisposedException.ThrowIf(disposed,this); if(definition.AttemptLimit is <1 or >20||definition.UnlockDuration<=TimeSpan.Zero||!Factors(definition.Policy).SequenceEqual(definition.References.Keys))throw new ArgumentException("Invalid lock definition."); states[definition.Target]=new(definition); }
    State Get(string target) {ObjectDisposedException.ThrowIf(disposed,this);return states.TryGetValue(target,out var s)?s:throw new KeyNotFoundException();}
    void Refresh(State s) {if(s.Waiting is {} wait && now()>=wait){s.Waiting=null;s.Attempts=s.Definition.AttemptLimit;}}
    public LockStatus Status(string target) {var s=Get(target);Refresh(s);return new(s.Unlocked is null || now()>=s.Unlocked,s.Attempts,s.Escalation,s.Waiting);}
    public bool Authenticate(string target,IReadOnlyList<string> orderedAnswers)
    {
        var s=Get(target);Refresh(s);if(s.Waiting!=null)return false;
        var factors=Factors(s.Definition.Policy);bool valid=orderedAnswers.Count==factors.Count;
        for(int i=0;i<factors.Count;i++) {var stored=vault.Get(s.Definition.References[factors[i]]);bool match=false; if(stored!=null&&i<orderedAnswers.Count) {try {if(factors[i]==LockFactor.Totp){var bytes=Convert.FromBase64String(stored);try{match=Totp.Verify(bytes,orderedAnswers[i],now());}finally{CryptographicOperations.ZeroMemory(bytes);}}else{var hash=JsonSerializer.Deserialize<PasswordHash>(stored)!;var actual=Rfc2898DeriveBytes.Pbkdf2(orderedAnswers[i],Convert.FromBase64String(hash.Salt),210000,HashAlgorithmName.SHA256,32);match=CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(hash.Hash));}}catch(FormatException){match=false;}catch(JsonException){match=false;} } valid &= match; }
        if(valid){s.Unlocked=now()+s.Definition.UnlockDuration;s.Attempts=s.Definition.AttemptLimit;s.Escalation=0;return true;}
        if(--s.Attempts<=0){s.Escalation=Math.Min(16,s.Escalation+1);s.Waiting=now()+TimeSpan.FromSeconds(Math.Min(3600,30*Math.Pow(2,s.Escalation-1)));}return false;
    }
    public void Relock(string target)=>Get(target).Unlocked=null;
    internal void ClearWaiting(string target){var s=Get(target);Refresh(s);if(s.Waiting!=null){s.Waiting=null;s.Attempts=s.Definition.AttemptLimit;}}
    public void Remove(string target){var s=Get(target);foreach(var reference in s.Definition.References.Values)vault.Delete(reference);states.Remove(target);}
    public void Dispose(){foreach(var s in states.Values)s.Unlocked=null;states.Clear();disposed=true;}
    sealed record PasswordHash(string Salt,string Hash);
}
