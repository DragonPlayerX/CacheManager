using System;
using System.Threading.Tasks;

namespace CacheManager.Tasks
{
    public static class TaskProvider
    {
        public static readonly AwaitProvider MainThreadQueue = new AwaitProvider("CacheManager.MainThread");

        public static AwaitProvider.YieldAwaitable YieldToMainThread() => MainThreadQueue.Yield();

        public static void NoAwait(this Task task, Action onComplete = null)
        {
            task.ContinueWith(t =>
            {
                onComplete?.Invoke();
                if (t.IsFaulted)
                    CacheManagerMod.Logger.Error("Task raised exception: " + t.Exception);
            });
        }
    }
}
