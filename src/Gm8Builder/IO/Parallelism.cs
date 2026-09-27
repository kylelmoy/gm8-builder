using System.Runtime.ExceptionServices;

namespace Gm8Builder.IO;

public static class Parallelism
{
    /// <summary>Parallel.For that throws the first failure itself rather than an AggregateException.</summary>
    public static void For(int count, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, body);
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }
}
