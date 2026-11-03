using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AlbumArtTool.Core
{
    internal static class Concurrency
    {
        // Parallel.For with plain exception semantics: cancellation surfaces as
        // OperationCanceledException and any other failure is rethrown unwrapped.
        internal static void For(int count, int degree, CancellationToken token, Action<int> body)
        {
            token.ThrowIfCancellationRequested();
            if (count == 0) return;
            if (count == 1 || degree <= 1) { for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); body(i); } return; }
            try { Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token }, body); }
            catch (AggregateException e)
            {
                var inner = e.Flatten().InnerExceptions;
                if (inner.All(x => x is OperationCanceledException)) throw new OperationCanceledException(token);
                ExceptionDispatchInfo.Capture(inner.First(x => !(x is OperationCanceledException))).Throw();
            }
        }
    }
}
