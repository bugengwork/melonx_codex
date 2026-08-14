using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.Memory;
using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Headless.SDL2
{
    [Flags]
    internal enum EmulationLifecycleMilestone : long
    {
        None = 0,
        GpuProducerDone = 1L << 0,
        SystemDisposed = 1L << 1,
        GpuDisposed = 1L << 2,
        RenderJoined = 1L << 3,
        Completed = 1L << 4,
        FirstFrame = 1L << 5,
    }

    internal sealed class EmulationLifecycleDiagnostics
    {
        public const long SchemaVersion = 1;
        public const long RequiredTeardownMilestones =
            (long)(EmulationLifecycleMilestone.GpuProducerDone |
                   EmulationLifecycleMilestone.SystemDisposed |
                   EmulationLifecycleMilestone.GpuDisposed |
                   EmulationLifecycleMilestone.RenderJoined |
                   EmulationLifecycleMilestone.Completed);

        public long SessionId { get; }
        public long State { get; }
        public long MilestoneMask { get; }
        public long RequiredMilestoneMask { get; }
        public long OrderViolationCount { get; }
        public long TeardownFailed { get; }
        public long ResultCode { get; }
        public long PhysicalFootprint { get; }

        public EmulationLifecycleDiagnostics(
            long sessionId,
            EmulationSessionState state,
            long milestoneMask,
            long requiredMilestoneMask,
            int orderViolationCount,
            bool teardownFailed,
            int resultCode,
            long physicalFootprint)
        {
            SessionId = sessionId;
            State = (long)state;
            MilestoneMask = milestoneMask;
            RequiredMilestoneMask = requiredMilestoneMask;
            OrderViolationCount = orderViolationCount;
            TeardownFailed = teardownFailed ? 1 : 0;
            ResultCode = resultCode;
            PhysicalFootprint = physicalFootprint;
        }
    }

    internal enum EmulationSessionState
    {
        Starting,
        Running,
        StopRequested,
        GpuProducerDone,
        SystemDisposed,
        GpuDisposing,
        Completed,
    }

    internal sealed class EmulationSession
    {
        private const int TaskVmInfoFlavor = 22;

        [StructLayout(LayoutKind.Sequential)]
        private struct TaskVmInfo
        {
            public ulong VirtualSize;
            public int RegionCount;
            public int PageSize;
            public ulong ResidentSize;
            public ulong ResidentSizePeak;
            public ulong Device;
            public ulong DevicePeak;
            public ulong Internal;
            public ulong InternalPeak;
            public ulong External;
            public ulong ExternalPeak;
            public ulong Reusable;
            public ulong ReusablePeak;
            public ulong PurgeableVolatilePmap;
            public ulong PurgeableVolatileResident;
            public ulong PurgeableVolatileVirtual;
            public ulong Compressed;
            public ulong CompressedPeak;
            public ulong CompressedLifetime;
            public ulong PhysFootprint;
        }

        private int _state = (int)EmulationSessionState.Starting;
        private int _stopRequested;
        private int _controlAccessClosed;
        private readonly object _resourceLock = new();
        private readonly object _diagnosticsLock = new();
        private WindowBase _window;
        private Switch _emulationContext;
        private IRenderer _renderer;
        private IntPtr _nativeMetalLayer;
        private ExceptionDispatchInfo _fault;
        private ExceptionDispatchInfo _teardownFault;
        private long _lifecycleMilestoneMask;
        private int _nextRequiredMilestone;
        private int _lifecycleOrderViolationCount;
        private bool _gpuLifecycleExpected;

        public long Id { get; }

        public EmulationSessionState State => (EmulationSessionState)Volatile.Read(ref _state);

        public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

        public ExceptionDispatchInfo Fault => Volatile.Read(ref _fault);

        public ExceptionDispatchInfo TeardownFault => Volatile.Read(ref _teardownFault);

        public bool HasTeardownFailure => TeardownFault != null;

        public EmulationSession(long id)
        {
            Id = id;
            LogSnapshot(EmulationSessionState.Starting.ToString());
        }

        public void AttachWindow(WindowBase window)
        {
            ArgumentNullException.ThrowIfNull(window);

            lock (_resourceLock)
            {
                if (_window != null)
                {
                    throw new InvalidOperationException($"Emulation session {Id} already has a window.");
                }

                _window = window;
            }

            if (IsStopRequested)
            {
                window.RequestStop();
            }
        }

        public void AttachEmulationContext(Switch emulationContext)
        {
            ArgumentNullException.ThrowIfNull(emulationContext);

            lock (_resourceLock)
            {
                if (_emulationContext != null)
                {
                    throw new InvalidOperationException($"Emulation session {Id} already has an emulation context.");
                }

                _emulationContext = emulationContext;
            }
        }

        public void TrackRenderer(IRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(renderer);

            lock (_resourceLock)
            {
                if (State != EmulationSessionState.Completed)
                {
                    _renderer = renderer;

                    lock (_diagnosticsLock)
                    {
                        _gpuLifecycleExpected = true;
                    }
                }
            }
        }

        public void CloseControlAccess()
        {
            lock (_resourceLock)
            {
                Volatile.Write(ref _controlAccessClosed, 1);
            }
        }

        public bool TryUseWindow(Action<WindowBase> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_resourceLock)
            {
                if (Volatile.Read(ref _controlAccessClosed) != 0 || _window == null)
                {
                    return false;
                }

                action(_window);
                return true;
            }
        }

        public bool TryUseWindow<TResult>(Func<WindowBase, TResult> action, out TResult result)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_resourceLock)
            {
                if (Volatile.Read(ref _controlAccessClosed) != 0 || _window == null)
                {
                    result = default;
                    return false;
                }

                result = action(_window);
                return true;
            }
        }

        public bool TryUseEmulationContext(Action<Switch> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_resourceLock)
            {
                if (Volatile.Read(ref _controlAccessClosed) != 0 || _emulationContext == null)
                {
                    return false;
                }

                action(_emulationContext);
                return true;
            }
        }

        public bool TryUseEmulationContext<TResult>(Func<Switch, TResult> action, out TResult result)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_resourceLock)
            {
                if (Volatile.Read(ref _controlAccessClosed) != 0 || _emulationContext == null)
                {
                    result = default;
                    return false;
                }

                result = action(_emulationContext);
                return true;
            }
        }

        public void SetNativeMetalLayer(IntPtr layer)
        {
            lock (_resourceLock)
            {
                if (State == EmulationSessionState.Completed ||
                    Volatile.Read(ref _controlAccessClosed) != 0)
                {
                    return;
                }

                _nativeMetalLayer = layer;
            }
        }

        public IntPtr GetNativeMetalLayer()
        {
            lock (_resourceLock)
            {
                return _nativeMetalLayer;
            }
        }

        public void MarkRunning()
        {
            TryTransition(EmulationSessionState.Starting, EmulationSessionState.Running);

            if (IsStopRequested)
            {
                MarkStopRequested();
                TryUseWindow(window => window.RequestStop());
            }
        }

        public bool RequestStop()
        {
            bool firstRequest = Interlocked.Exchange(ref _stopRequested, 1) == 0;

            MarkStopRequested();
            TryUseWindow(window => window.RequestStop());

            return firstRequest;
        }

        public void MarkStopRequested()
        {
            while (true)
            {
                EmulationSessionState state = State;

                if ((int)state >= (int)EmulationSessionState.StopRequested)
                {
                    return;
                }

                if (TryTransition(state, EmulationSessionState.StopRequested))
                {
                    return;
                }
            }
        }

        public void MarkGpuProducerDone()
        {
            MarkStopRequested();

            if (TryTransition(EmulationSessionState.StopRequested, EmulationSessionState.GpuProducerDone))
            {
                RecordRequiredMilestone(EmulationLifecycleMilestone.GpuProducerDone, 0);
            }
        }

        public void MarkSystemDisposed()
        {
            MarkGpuProducerDone();

            if (TryTransition(EmulationSessionState.GpuProducerDone, EmulationSessionState.SystemDisposed))
            {
                RecordRequiredMilestone(EmulationLifecycleMilestone.SystemDisposed, 1);
            }
        }

        public void MarkGpuDisposing()
        {
            MarkSystemDisposed();
            TryTransition(EmulationSessionState.SystemDisposed, EmulationSessionState.GpuDisposing);
        }

        public bool MarkCompleted()
        {
            lock (_resourceLock)
            {
                if (HasTeardownFailure ||
                    Interlocked.CompareExchange(
                        ref _state,
                        (int)EmulationSessionState.Completed,
                        (int)EmulationSessionState.GpuDisposing) != (int)EmulationSessionState.GpuDisposing)
                {
                    return false;
                }

                _window = null;
                _emulationContext = null;
                _renderer = null;
                _nativeMetalLayer = IntPtr.Zero;
            }

            RecordCompletionMilestone();
            LogSnapshot($"{EmulationSessionState.GpuDisposing}->{EmulationSessionState.Completed}");

            return true;
        }

        public bool TryBeginRestart()
        {
            lock (_resourceLock)
            {
                if (IsStopRequested ||
                    HasTeardownFailure ||
                    !TryTransition(
                        EmulationSessionState.GpuDisposing,
                        EmulationSessionState.Starting))
                {
                    return false;
                }

                // A user-channel restart reuses the native entrypoint/session but
                // only after the preceding cycle completed teardown successfully.
                _window = null;
                _emulationContext = null;
                _renderer = null;
                Volatile.Write(ref _controlAccessClosed, 0);

                lock (_diagnosticsLock)
                {
                    _lifecycleMilestoneMask = 0;
                    _nextRequiredMilestone = 0;
                    _lifecycleOrderViolationCount = 0;
                    _gpuLifecycleExpected = false;
                }

                return true;
            }
        }

        public void RecordFault(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(exception), null);
            LogSnapshot($"Fault:{exception.GetType().Name}");
            RequestStop();
        }

        public void RecordTeardownFault(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            Interlocked.CompareExchange(
                ref _teardownFault,
                ExceptionDispatchInfo.Capture(exception),
                null);
            Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(exception), null);
            LogSnapshot($"TeardownFault:{exception.GetType().Name}");
            RequestStop();
        }

        public void LogMilestone(string milestone)
        {
            ArgumentException.ThrowIfNullOrEmpty(milestone);

            switch (milestone)
            {
                case "FirstFrame":
                    RecordInformationalMilestone(EmulationLifecycleMilestone.FirstFrame);
                    break;
                case "GpuDisposed":
                    RecordRequiredMilestone(EmulationLifecycleMilestone.GpuDisposed, 2);
                    break;
                case "RenderJoined":
                    RecordRequiredMilestone(EmulationLifecycleMilestone.RenderJoined, 3);
                    break;
            }

            LogSnapshot(milestone);
        }

        public long GetLifecycleMilestoneMask()
        {
            lock (_diagnosticsLock)
            {
                return _lifecycleMilestoneMask;
            }
        }

        public EmulationLifecycleDiagnostics CreateDiagnosticsSnapshot(int resultCode)
        {
            lock (_diagnosticsLock)
            {
                return new EmulationLifecycleDiagnostics(
                    Id,
                    State,
                    _lifecycleMilestoneMask,
                    GetRequiredMilestoneMaskNoLock(),
                    _lifecycleOrderViolationCount,
                    HasTeardownFailure,
                    resultCode,
                    GetPhysicalFootprint());
            }
        }

        private bool TryTransition(EmulationSessionState from, EmulationSessionState to)
        {
            if (Interlocked.CompareExchange(ref _state, (int)to, (int)from) != (int)from)
            {
                return false;
            }

            LogSnapshot($"{from}->{to}");

            return true;
        }

        private void RecordInformationalMilestone(EmulationLifecycleMilestone milestone)
        {
            lock (_diagnosticsLock)
            {
                _lifecycleMilestoneMask |= (long)milestone;
            }
        }

        private void RecordRequiredMilestone(EmulationLifecycleMilestone milestone, int ordinal)
        {
            lock (_diagnosticsLock)
            {
                RecordRequiredMilestoneNoLock(milestone, ordinal);
            }
        }

        private void RecordCompletionMilestone()
        {
            lock (_diagnosticsLock)
            {
                if (_gpuLifecycleExpected)
                {
                    RecordRequiredMilestoneNoLock(
                        EmulationLifecycleMilestone.Completed,
                        4);
                    return;
                }

                long bit = (long)EmulationLifecycleMilestone.Completed;

                if ((_lifecycleMilestoneMask & bit) != 0)
                {
                    _lifecycleOrderViolationCount++;
                    return;
                }

                if (_nextRequiredMilestone != 2)
                {
                    _lifecycleOrderViolationCount++;
                }

                _lifecycleMilestoneMask |= bit;
                _nextRequiredMilestone = 5;
            }
        }

        private void RecordRequiredMilestoneNoLock(
            EmulationLifecycleMilestone milestone,
            int ordinal)
        {
            long bit = (long)milestone;

            if ((_lifecycleMilestoneMask & bit) != 0)
            {
                _lifecycleOrderViolationCount++;
                return;
            }

            if (ordinal != _nextRequiredMilestone)
            {
                _lifecycleOrderViolationCount++;
            }

            _lifecycleMilestoneMask |= bit;

            while (_nextRequiredMilestone < 5 &&
                   (_lifecycleMilestoneMask & (1L << _nextRequiredMilestone)) != 0)
            {
                _nextRequiredMilestone++;
            }
        }

        private long GetRequiredMilestoneMaskNoLock()
        {
            if (_gpuLifecycleExpected)
            {
                return EmulationLifecycleDiagnostics.RequiredTeardownMilestones;
            }

            return (long)(
                EmulationLifecycleMilestone.GpuProducerDone |
                EmulationLifecycleMilestone.SystemDisposed |
                EmulationLifecycleMilestone.Completed);
        }

        private void LogSnapshot(string stage)
        {
            try
            {
                long managedBytes = GC.GetTotalMemory(false);
                long footprintBytes = GetPhysicalFootprint();

                Logger.Notice.Print(
                    LogClass.Application,
                    $"Emulation lifecycle: sessionId={Id}; stage={stage}; state={State}; " +
                    $"thread={Environment.CurrentManagedThreadId}; gpuThreads={ThreadedRenderer.ActiveGpuThreadCount}; " +
                    $"renderThreads={WindowBase.ActiveRenderThreadCount}; managed={managedBytes}; phys_footprint={footprintBytes}; " +
                    $"vulkanInstance={VulkanRenderer.InstanceCreatedCount}/{VulkanRenderer.InstanceDestroyedCount}; " +
                    $"vulkanSurface={VulkanRenderer.SurfaceCreatedCount}/{VulkanRenderer.SurfaceDestroyedCount}; " +
                    $"vulkanDevice={VulkanRenderer.DeviceCreatedCount}/{VulkanRenderer.DeviceDestroyedCount}; " +
                    $"jitMappingsCreated={DualMappedJitAllocator.SuccessfulMappingCount}.");
            }
            catch
            {
                // Lifecycle telemetry must never affect teardown or cross the native ABI.
            }
        }

        private static long GetPhysicalFootprint()
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                try
                {
                    TaskVmInfo taskVmInfo = default;
                    uint count = (uint)(Marshal.SizeOf<TaskVmInfo>() / sizeof(uint));

                    if (TaskInfo(MachTaskSelf(), TaskVmInfoFlavor, ref taskVmInfo, ref count) == 0)
                    {
                        return checked((long)taskVmInfo.PhysFootprint);
                    }
                }
                catch
                {
                }
            }

            try
            {
                return Environment.WorkingSet;
            }
            catch
            {
                return -1;
            }
        }

        [DllImport("libc", EntryPoint = "mach_task_self")]
        private static extern uint MachTaskSelf();

        [DllImport("libc", EntryPoint = "task_info")]
        private static extern int TaskInfo(uint task, int flavor, ref TaskVmInfo taskInfo, ref uint taskInfoCount);
    }
}
