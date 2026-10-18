using System;
using System.Diagnostics;
using System.Threading;

namespace RimMT2
{
    internal interface ISnapshotWorkItem
    {
        string ModuleName { get; }
        void ExecuteOnWorker();
        void CommitOnMainThread();
        void OnQueueRejected();
    }

    internal abstract class SnapshotWorkItem<TResult> : ISnapshotWorkItem
    {
        private TResult result;
        private Exception workerError;
        private long workerElapsedTicks;

        public abstract string ModuleName { get; }

        public void ExecuteOnWorker()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                result = ComputeSnapshot();
                Interlocked.Increment(ref FabricMetrics.WorkerCompleted);
            }
            catch (Exception exception)
            {
                workerError = exception;
                Interlocked.Increment(ref FabricMetrics.WorkerFailed);
            }
            finally
            {
                stopwatch.Stop();
                workerElapsedTicks = stopwatch.ElapsedTicks;
                Interlocked.Add(ref FabricMetrics.WorkerTicks, workerElapsedTicks);
            }
        }

        public void CommitOnMainThread()
        {
            MainThreadGuard.RequireMainThread(ModuleName + ".Commit");
            if (workerError != null)
            {
                OnWorkerFailure(workerError);
                Interlocked.Increment(ref FabricMetrics.CommitDiscarded);
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (CanCommit(result))
                {
                    CommitResult(result);
                    Interlocked.Increment(ref FabricMetrics.CommitSucceeded);
                }
                else
                {
                    OnStaleResult(result);
                    Interlocked.Increment(ref FabricMetrics.CommitDiscarded);
                }
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref FabricMetrics.CommitFailed);
                OnCommitFailure(exception);
            }
            finally
            {
                stopwatch.Stop();
                Interlocked.Add(ref FabricMetrics.CommitTicks, stopwatch.ElapsedTicks);
            }
        }

        public virtual void OnQueueRejected()
        {
        }

        protected abstract TResult ComputeSnapshot();
        protected abstract bool CanCommit(TResult computed);
        protected abstract void CommitResult(TResult computed);

        protected virtual void OnStaleResult(TResult computed)
        {
        }

        protected virtual void OnWorkerFailure(Exception exception)
        {
            Verse.Log.Error("[RimMT 2.0] " + ModuleName + " worker failed; only this result was discarded. " + exception);
        }

        protected virtual void OnCommitFailure(Exception exception)
        {
            Verse.Log.Error("[RimMT 2.0] " + ModuleName + " main-thread commit failed; only this result was discarded. " + exception);
        }
    }
}
