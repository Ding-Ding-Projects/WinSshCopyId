using System.Security.Cryptography;
namespace WinSshCopyId.Workspace.Security;
public enum LadderStage { Dishes, Sums, Moles, Clock }
public sealed record Mole(int Id,int Cell,int StartsMilliseconds,int EndsMilliseconds);
public sealed record MoleHit(int Id,int Cell,int AtMilliseconds);
public sealed record LadderChallenge(string Nonce,LadderStage Stage,string Prompt,IReadOnlyList<string> Choices,IReadOnlyList<Mole> Moles,DateTimeOffset ExpiresAt,int RoundMilliseconds=0);
public sealed class UnlockLadder(ToyLockEngine locks,Func<DateTimeOffset>? clock=null)
{
    readonly Func<DateTimeOffset> now=clock??(()=>DateTimeOffset.UtcNow);
    readonly Dictionary<string,Round> rounds=new(StringComparer.Ordinal);
    readonly Dictionary<string,Queue<DateTimeOffset>> skips=new(StringComparer.Ordinal);
    sealed class Round(DateTimeOffset wait,LadderStage stage){public DateTimeOffset Wait=wait;public LadderStage Stage=stage;public int WrongDishes,Sums;public LadderChallenge? Challenge;public string Answer="";public DateTimeOffset Issued;public HashSet<int> Hits=new();}
    public LadderChallenge? Next(string target,bool schoolMode=false)
    {
        var status=locks.Status(target);if(status.WaitingUntil is not {} wait)return null;
        if(!skips.TryGetValue(target,out var budget))skips[target]=budget=new();while(budget.TryPeek(out var time)&&now()-time>=TimeSpan.FromHours(1))budget.Dequeue();
        if(budget.Count>=3)return null;
        if(!rounds.TryGetValue(target,out var r)||r.Wait!=wait) rounds[target]=r=new(wait,schoolMode?LadderStage.Sums:LadderStage.Dishes);
        if(schoolMode&&r.Stage==LadderStage.Dishes)r.Stage=LadderStage.Sums;
        if(r.Stage==LadderStage.Clock)return null;
        if(r.Challenge is {} existing&&existing.ExpiresAt>now())return existing;
        r.Issued=now();r.Hits.Clear();string prompt;string[] choices=[];Mole[] moles=[];int duration=0;
        if(r.Stage==LadderStage.Dishes){choices=["Har gow","Siu mai","Cheung fun","Char siu bao"];int answer=RandomNumberGenerator.GetInt32(4);prompt="Select the dish named: "+choices[answer];r.Answer=answer.ToString();}
        else if(r.Stage==LadderStage.Sums){int a=RandomNumberGenerator.GetInt32(1,50),b=RandomNumberGenerator.GetInt32(1,50);prompt=$"{a} + {b}";r.Answer=(a+b).ToString();}
        else {duration=10000;prompt="Hit at least six distinct visible moles.";moles=Enumerable.Range(0,10).Select(i=>new Mole(i,RandomNumberGenerator.GetInt32(9),i*900,i*900+800)).ToArray();}
        return r.Challenge=new(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),r.Stage,prompt,choices,moles,now()+TimeSpan.FromSeconds(30),duration);
    }
    public bool Hit(string target,string nonce,int moleId,int cell)
    {
        if(!rounds.TryGetValue(target,out var r)||r.Challenge is not {} c||c.Nonce!=nonce||c.Stage!=LadderStage.Moles||now()>=c.ExpiresAt||locks.Status(target).WaitingUntil!=r.Wait)return false;
        var elapsed=(now()-r.Issued).TotalMilliseconds;
        var mole=c.Moles.FirstOrDefault(m=>m.Id==moleId);
        return mole!=null&&mole.Cell==cell&&elapsed>=mole.StartsMilliseconds&&elapsed<mole.EndsMilliseconds&&r.Hits.Add(moleId);
    }
    public bool Submit(string target,string nonce,string? answer=null,IReadOnlyList<MoleHit>? hits=null)
    {
        if(!rounds.TryGetValue(target,out var r)||r.Challenge is not {} c||!string.Equals(c.Nonce,nonce,StringComparison.Ordinal))return false;
        r.Challenge=null; // Consume before grading, including malformed and expired submissions.
        if(c.ExpiresAt<=now()||locks.Status(target).WaitingUntil!=r.Wait)return false;
        bool correct;
        if(c.Stage==LadderStage.Moles){correct=answer==null&&hits==null&&now()-r.Issued>=TimeSpan.FromMilliseconds(c.RoundMilliseconds)&&r.Hits.Count>=6;if(!correct)r.Stage=LadderStage.Clock;}
        else {correct=hits==null&&answer==r.Answer;if(!correct){if(c.Stage==LadderStage.Dishes){if(++r.WrongDishes>=5)r.Stage=LadderStage.Sums;}else r.Stage=LadderStage.Moles;}else if(c.Stage==LadderStage.Sums&&++r.Sums<10)return false;}
        if(!correct)return false;
        if(!skips.TryGetValue(target,out var budget))skips[target]=budget=new();while(budget.TryPeek(out var time)&&now()-time>=TimeSpan.FromHours(1))budget.Dequeue();if(budget.Count>=3)return false;
        budget.Enqueue(now());locks.ClearWaiting(target);rounds.Remove(target);return true;
    }
}

