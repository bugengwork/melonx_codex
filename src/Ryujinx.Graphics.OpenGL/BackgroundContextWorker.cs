using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ryujinx.Graphics.OpenGL
{
    unsafe class BackgroundContextWorker : IDisposable
    {
        private sealed class WorkItem
        {
            private readonly Action _action;

            public ManualResetEventSlim Completed { get; } = new(false);
            public ExceptionDispatchInfo Failure { get; private set; }

            public WorkItem(Action action)
            {
                _action = action;
            }

            public void Run()
            {
                try
                {
                    _action();
                }
                catch (Exception exception)
                {
                    Failure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    Completed.Set();
                }
            }

            public void Fail(Exception exception)
            {
                Failure ??= ExceptionDispatchInfo.Capture(exception);
                Completed.Set();
            }
        }

        [ThreadStatic]
        public static bool InBackground;
        private readonly Thread _thread;
        private int _running;
        private int _disposeState;
        private ExceptionDispatchInfo _failure;
        private readonly object _disposeLock = new();

        private readonly AutoResetEvent _signal;
        private readonly Queue<WorkItem> _work;
        private readonly IOpenGLContext _backgroundContext;

        public BackgroundContextWorker(IOpenGLContext backgroundContext)
        {
            _backgroundContext = backgroundContext;
            _running = 1;

            _signal = new AutoResetEvent(false);
            _work = new Queue<WorkItem>();

            _thread = new Thread(Run);

            try
            {
                _thread.Start();
            }
            catch
            {
                Volatile.Write(ref _running, 0);
                _signal.Dispose();
                _backgroundContext.Dispose();
                throw;
            }
        }

        public bool HasContext() => _backgroundContext.HasContext();

        private void Run()
        {
            InBackground = true;

            try
            {
                _backgroundContext.MakeCurrent();

                while (Volatile.Read(ref _running) != 0)
                {
                    WorkItem workItem;

                    lock (_work)
                    {
                        _work.TryDequeue(out workItem);
                    }

                    if (workItem != null)
                    {
                        workItem.Run();
                    }
                    else
                    {
                        _signal.WaitOne();
                    }
                }
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
            }
            finally
            {
                Volatile.Write(ref _running, 0);

                try
                {
                    _backgroundContext.Dispose();
                }
                catch (Exception exception)
                {
                    RecordFailure(exception);
                }

                Exception failure = Volatile.Read(ref _failure)?.SourceException ??
                    new ObjectDisposedException(nameof(BackgroundContextWorker));

                lock (_work)
                {
                    while (_work.TryDequeue(out WorkItem workItem))
                    {
                        workItem.Fail(failure);
                    }
                }

                InBackground = false;
            }
        }

        public void Invoke(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            WorkItem workItem = new(action);

            try
            {
                lock (_work)
                {
                    ThrowIfUnavailable();
                    _work.Enqueue(workItem);
                }

                try
                {
                    _signal.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose joins the worker before closing the signal. A work
                    // item queued in this race is failed by the worker's finally.
                }

                workItem.Completed.Wait();

                workItem.Failure?.Throw();
                ThrowIfFaulted();
            }
            finally
            {
                workItem.Completed.Dispose();
            }
        }

        public void Dispose()
        {
            if (Thread.CurrentThread == _thread)
            {
                throw new InvalidOperationException(
                    "The background context worker cannot dispose itself.");
            }

            lock (_disposeLock)
            {
                if (_disposeState == 2)
                {
                    ThrowIfFaulted();
                    return;
                }

                if (_disposeState == 0)
                {
                    _disposeState = 1;

                    try
                    {
                        Volatile.Write(ref _running, 0);
                        _signal.Set();
                        _thread.Join();
                        _signal.Dispose();
                    }
                    finally
                    {
                        _disposeState = 2;
                    }
                }

                ThrowIfFaulted();
            }
        }

        private void RecordFailure(Exception exception)
        {
            Interlocked.CompareExchange(
                ref _failure,
                ExceptionDispatchInfo.Capture(exception),
                null);
        }

        private void ThrowIfUnavailable()
        {
            ThrowIfFaulted();

            if (Volatile.Read(ref _running) == 0)
            {
                throw new ObjectDisposedException(nameof(BackgroundContextWorker));
            }
        }

        private void ThrowIfFaulted()
        {
            Volatile.Read(ref _failure)?.Throw();
        }
    }
}
