﻿using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Sir.Core
{
    /// <summary>
    /// Enque items and forget about it. They will be consumed by other threads.
    /// Call ProducerConsumerQueue.Complete() to have consumer threads join main.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ProducerConsumerQueue<T> : IDisposable
    {
        private BlockingCollection<T> _queue;
        private readonly int _numOfConsumers;
        private readonly Action<T> _consumingAction;
        private Task[] _consumers;
        private bool _completed;
        private bool _started;
        private bool _joining;

        public int Count { get { return _queue.Count; } }

        public bool IsCompleted { get { return _queue == null || _queue.IsCompleted; } }

        public ProducerConsumerQueue(Action<T> consumingAction) : this(consumingAction, 1) { }

        public ProducerConsumerQueue(Action<T> consumingAction, int numOfConsumers, bool startConsumingImmediately = true)
        {
            _queue = new BlockingCollection<T>();
            _numOfConsumers = numOfConsumers;
            _consumingAction = consumingAction;

            if (startConsumingImmediately)
            {
                Start();
            }
        }

        public void Start()
        {
            if (_started)
                return;

            _consumers = new Task[_numOfConsumers];

            for (int i = 0; i < _numOfConsumers; i++)
            {
                _consumers[i] = Task.Run(() =>
                {
                    while (!_queue.IsCompleted)
                    {
                        try
                        {
                            var item = _queue.Take();

                            _consumingAction(item);
                        }
                        catch (InvalidOperationException) { }
                    }
                });
            }

            _started = true;
        }

        public void Enqueue(T item)
        {
            _queue.Add(item);
        }

        public void Join()
        {
            if (_joining || _completed)
                return;

            if (!_started)
                return;

            _joining = true;

            _queue.CompleteAdding();

            Task.WaitAll(_consumers);

            _queue.Dispose();
            _queue = null;
            _completed = true;
            _joining = false;
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Join();
            }
        }
    }
}
