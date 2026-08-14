using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    class SyncMap : IDisposable
    {
        private readonly HashSet<ulong> _inFlight = new();
        private readonly AutoResetEvent _inFlightChanged = new(false);

        internal void CreateSyncHandle(ulong id)
        {
            lock (_inFlight)
            {
                _inFlight.Add(id);
            }
        }

        internal void AssignSync(ulong id)
        {
            lock (_inFlight)
            {
                _inFlight.Remove(id);
            }

            _inFlightChanged.Set();
        }

        internal void WaitSyncAvailability(ulong id, Action consumerCheck)
        {
            // Blocks until the handle is available.

            bool signal = false;

            while (true)
            {
                lock (_inFlight)
                {
                    if (!_inFlight.Contains(id))
                    {
                        break;
                    }
                }

                _inFlightChanged.WaitOne(10);
                signal = true;

                lock (_inFlight)
                {
                    if (!_inFlight.Contains(id))
                    {
                        break;
                    }
                }

                consumerCheck();
            }

            if (signal)
            {
                // Signal other threads which might still be waiting.
                _inFlightChanged.Set();
            }
        }

        internal void SignalWaiters()
        {
            try
            {
                _inFlightChanged.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _inFlightChanged.Dispose();
        }
    }
}
