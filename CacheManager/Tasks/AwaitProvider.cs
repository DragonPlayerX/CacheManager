﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CacheManager.Tasks
{
    public class AwaitProvider
    {
        private readonly Queue<Action> queue = new Queue<Action>();

        public readonly string Name;

        public AwaitProvider(string name) => Name = name;

        public void Dequeue()
        {
            if (queue.Count == 0)
                return;

            List<Action> actions;

            lock (queue)
            {
                actions = queue.ToList();
                queue.Clear();
            }

            foreach (Action action in actions)
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception e)
                {
                    CacheManagerMod.Logger.Error(e);
                }
            }
        }

        public void Add(Action action) => queue.Enqueue(action);

        public YieldAwaitable Yield() => new YieldAwaitable(queue);

        public readonly struct YieldAwaitable : INotifyCompletion
        {
            private readonly Queue<Action> queue;

            public YieldAwaitable(Queue<Action> queue) => this.queue = queue;

            public bool IsCompleted => false;

            public YieldAwaitable GetAwaiter() => this;

            public void GetResult() { }

            public void OnCompleted(Action continuation)
            {
                lock (queue)
                    queue.Enqueue(continuation);
            }
        }
    }
}
