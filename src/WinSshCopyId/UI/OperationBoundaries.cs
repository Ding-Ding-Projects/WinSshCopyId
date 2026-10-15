namespace WinSshCopyId.UI;

/// <summary>A profile reference is opaque data. Only explicit selection can supply a usable key.</summary>
public sealed record ProfileKeySelection<T> where T:class
{
    public string? Reference {get;private init;}
    public T? Selected {get;private init;}
    private ProfileKeySelection(string? reference,T? selected){Reference=reference;Selected=selected;}
    public static ProfileKeySelection<T> FromProfile(string? reference)=>new(reference,null);
    public ProfileKeySelection<T> Choose(T selected,string reference)=>new(reference,selected??throw new ArgumentNullException(nameof(selected)));
    public ProfileKeySelection<T> ClearSelection()=>new(Reference,null);
}

public enum RetryPhaseResult {Completed,Cancelled,Declined}
public static class RetryPhaseCoordinator
{
    /// <summary>One cancellation identity covers verification, review and deployment, including phase boundaries.</summary>
    public static async Task<RetryPhaseResult> RunAsync(Func<CancellationToken,Task> verify,
        Func<CancellationToken,Task<bool>> review,Func<CancellationToken,Task> deploy,CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await verify(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var approved=await review(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if(!approved)return RetryPhaseResult.Declined;
            await deploy(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return RetryPhaseResult.Completed;
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){return RetryPhaseResult.Cancelled;}
    }
}
