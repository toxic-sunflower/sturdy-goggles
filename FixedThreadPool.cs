namespace Threading
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Threading;

    public sealed class FixedThreadPool
    {
        private readonly object _dequeueSyncRoot = new object();
        private readonly Thread[] _threads;

        private readonly Dictionary<Priority, ConcurrentQueue<ITask>> _queue = new Dictionary<Priority, ConcurrentQueue<ITask>>
        {
            {Priority.Low, new ConcurrentQueue<ITask>()},
            {Priority.Normal, new ConcurrentQueue<ITask>()},
            {Priority.High, new ConcurrentQueue<ITask>()}
        };

        private volatile int _queueBacklog;

        private int _highPriorityTaken;
        private bool _isStopRequested;


        public FixedThreadPool(int threadsCount)
        {
            if (threadsCount < 1)
                throw new ArgumentOutOfRangeException(nameof(threadsCount), threadsCount, "Value must be greater than 0");

            _threads = new Thread[threadsCount];
            
            for (var i = 0; i < _threads.Length; ++i)
            {
                var thread = new Thread(ThreadAction) {Name = $"{nameof(FixedThreadPool)}_{GetHashCode()}_{i}"};

                _threads[i] = thread;

                thread.Start();
            }
        }


        #region Methods

        public bool Execute(ITask task, Priority priority)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (_isStopRequested)
                return false;

            _queue[priority].Enqueue(task);

            Interlocked.Increment(ref _queueBacklog);

            return true;
        }

        public void Stop()
        {
            if (_isStopRequested)
                return;

            _isStopRequested = true;

            foreach (var thread in _threads)
            {
                thread.Join();
            }
        }

        #endregion

        #region Events

        public event EventHandler<ThreadExceptionEventArgs> UnhandledTaskError;

        #endregion

        #region Implementation

        private void ThreadAction()
        {
            while (!_isStopRequested || _queueBacklog != 0)
            {
                try
                {
                    DequeueNextTask()?.Execute();
                }
                catch (Exception x)
                {
                    if (UnhandledTaskError == null)
                        throw;
                    
                    UnhandledTaskError(this, new ThreadExceptionEventArgs(x));
                }
            }
        }

        private ITask DequeueNextTask()
        {
            lock (_dequeueSyncRoot)
            {
                ITask result = null;

                if ((_queue[Priority.Normal].Count != 0 && _highPriorityTaken >= 3) || (_queue[Priority.High].Count == 0 && _queue[Priority.Normal].Count != 0))
                {
                    if (_queue[Priority.Normal].TryDequeue(out result))
                    {
                        _highPriorityTaken = 0;
                    }
                }
                else if (_queue[Priority.High].Count != 0)
                {
                    if (_queue[Priority.High].TryDequeue(out result))
                    {
                        _highPriorityTaken++;
                    }
                }
                else if (_queue[Priority.Low].Count != 0)
                {
                    _queue[Priority.Low].TryDequeue(out result);
                }

                if (result != null)
                    Interlocked.Decrement(ref _queueBacklog);

                return result;
            }
        }

        #endregion
    }


    public interface ITask
    {
        void Execute();
    }

    public enum Priority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }
}
