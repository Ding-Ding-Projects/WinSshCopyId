using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinSshCopyId.Workspace.Security;

namespace WinSshCopyId.Workspace;

/// <summary>Serializes security operations and atomically persists encrypted definitions and rate budgets.</summary>
public sealed class WorkspaceSecuritySession : IDisposable
{
    private readonly IPrivateStorage storage;
    private readonly VaultDelegates vault;
    private readonly IClock clock;
    private readonly SemaphoreSlim serial = new(1);
    private readonly Dictionary<string,ToyLockDefinition> definitions = new(StringComparer.Ordinal);
    private ToyLockEngine locks;
    private UnlockLadder ladder;
    private byte[]? key;
    private bool disposed;
    private const string KeyReference="workspace/security-state-key-v1";
    public bool IsReady {get;private set;}
    public string? UnavailableReason {get;private set;}="Security state has not been loaded.";
    private sealed record StoredState(int Version,IReadOnlyList<ToyLockDefinition> Definitions,LockBudgetSnapshot Locks,LadderBudgetSnapshot Ladder);
    private sealed record Envelope(int Version,string Nonce,string Ciphertext,string Tag);
    public WorkspaceSecuritySession(IPrivateStorage storage,VaultDelegates vault,IClock clock)
    {this.storage=storage;this.vault=vault;this.clock=clock;locks=new(vault,()=>clock.UtcNow);ladder=new(locks,()=>clock.UtcNow);}
    private void Check(){ObjectDisposedException.ThrowIf(disposed,this);if(!IsReady)throw new InvalidOperationException(UnavailableReason);}
    public async Task<bool> LoadAsync(CancellationToken cancellationToken=default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);IsReady=false;
            var bytes=await storage.ReadAsync("security-state.json",cancellationToken);
            var encoded=vault.Get(KeyReference);
            if(encoded is null)
            {
                if(bytes is not null)throw new InvalidDataException("Security key is unavailable; existing state was retained.");
                key=RandomNumberGenerator.GetBytes(32);vault.Set(KeyReference,Convert.ToBase64String(key));
            }
            else {if(key is not null)CryptographicOperations.ZeroMemory(key);key=Convert.FromBase64String(encoded);if(key.Length!=32)throw new InvalidDataException("Security key is invalid.");}
            locks.Dispose();ladder.Dispose();definitions.Clear();locks=new(vault,()=>clock.UtcNow);ladder=new(locks,()=>clock.UtcNow);
            if(bytes is not null)
            {
                var envelope=JsonSerializer.Deserialize<Envelope>(bytes,WorkspaceSettingsStore.JsonOptions)??throw new InvalidDataException();
                if(envelope.Version!=1)throw new InvalidDataException("Unsupported security state.");
                var ciphertext=Convert.FromBase64String(envelope.Ciphertext);var plain=new byte[ciphertext.Length];
                try
                {
                    using(var aes=new AesGcm(key,16))aes.Decrypt(Convert.FromBase64String(envelope.Nonce),ciphertext,Convert.FromBase64String(envelope.Tag),plain,Encoding.UTF8.GetBytes("WinSshCopyId/security-state/v1"));
                    var state=JsonSerializer.Deserialize<StoredState>(plain,WorkspaceSettingsStore.JsonOptions)??throw new InvalidDataException();
                    if(state.Version!=1||state.Definitions.Count>1000)throw new InvalidDataException();
                    foreach(var definition in state.Definitions){locks.Load(definition);definitions.Add(definition.Target,definition);}
                    locks.ImportBudgets(state.Locks);ladder.ImportBudgets(state.Ladder);
                }
                finally{CryptographicOperations.ZeroMemory(plain);}
            }
            IsReady=true;UnavailableReason=null;return true;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or ArgumentException or InvalidDataException)
        {IsReady=false;UnavailableReason="Security state or its credential vault is unavailable. Existing state was retained; retry or use the documented local reset route.";return false;}
        finally{serial.Release();}
    }
    private async Task Save(CancellationToken cancellationToken)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(new StoredState(1,definitions.Values.ToArray(),locks.ExportBudgets(),ladder.ExportBudgets()),WorkspaceSettingsStore.JsonOptions);
        try
        {
            var nonce=RandomNumberGenerator.GetBytes(12);var encrypted=new byte[plain.Length];var tag=new byte[16];
            using(var aes=new AesGcm(key!,16))aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes("WinSshCopyId/security-state/v1"));
            await storage.WriteAtomicAsync("security-state.json",JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,Convert.ToBase64String(nonce),Convert.ToBase64String(encrypted),Convert.ToBase64String(tag))),cancellationToken);
        }
        catch{IsReady=false;UnavailableReason="Security state could not be saved; protected actions remain unavailable until a successful reload.";throw;}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private async Task<T> Mutation<T>(Func<T> operation,CancellationToken cancellationToken)
    {await serial.WaitAsync(cancellationToken);try{Check();var result=operation();await Save(cancellationToken);return result;}finally{serial.Release();}}
    public Task<ToyLockDefinition> CreateAsync(string target,LockPolicy policy,IReadOnlyList<string> credentials,TimeSpan duration,int attemptLimit=3,CancellationToken cancellationToken=default)=>
        Mutation(()=>{var definition=locks.Create(target,policy,credentials,duration,attemptLimit);definitions[target]=definition;return definition;},cancellationToken);
    public Task<bool> AuthenticateAsync(string target,IReadOnlyList<string> orderedAnswers,CancellationToken cancellationToken=default)=>Mutation(()=>locks.Authenticate(target,orderedAnswers),cancellationToken);
    public Task<LockStatus> StatusAsync(string target,CancellationToken cancellationToken=default)=>Mutation(()=>locks.Status(target),cancellationToken);
    public Task<bool> RelockAsync(string target,CancellationToken cancellationToken=default)=>Mutation(()=>{locks.Relock(target);return true;},cancellationToken);
    public Task<bool> RemoveAsync(string target,CancellationToken cancellationToken=default)=>Mutation(()=>{locks.Remove(target);definitions.Remove(target);return true;},cancellationToken);
    public Task<LadderChallenge?> NextChallengeAsync(string target,bool schoolMode=false,CancellationToken cancellationToken=default)=>Mutation(()=>ladder.Next(target,schoolMode),cancellationToken);
    public Task<bool> HitAsync(string target,string nonce,int moleId,int cell,CancellationToken cancellationToken=default)=>Mutation(()=>ladder.Hit(target,nonce,moleId,cell),cancellationToken);
    public Task<bool> SubmitAsync(string target,string nonce,string? answer=null,CancellationToken cancellationToken=default)=>Mutation(()=>ladder.Submit(target,nonce,answer),cancellationToken);
    public void Dispose(){if(disposed)return;disposed=true;IsReady=false;locks.Dispose();ladder.Dispose();if(key is not null)CryptographicOperations.ZeroMemory(key);key=null;}
}
