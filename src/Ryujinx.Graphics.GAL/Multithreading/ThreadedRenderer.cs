using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// The ThreadedRenderer is a layer that can be put in front of any Renderer backend to make
    /// its processing happen on a separate thread, rather than intertwined with the GPU emulation.
    /// A new thread is created to handle the GPU command processing, separate from the renderer thread.
    /// Calls to the renderer, pipeline and resources are queued to happen on the renderer thread.
    /// </summary>
    public class ThreadedRenderer : IRenderer
    {
        private const int SpanPoolBytes = 4 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;
        private const int QueueCount = 10000;
        private const int RunLoopNotStarted = 0;
        private const int RunLoopRunning = 1;
        private const int RunLoopExited = 2;
        private const int DisposeActive = 0;
        private const int DisposeInProgress = 1;
        private const int DisposeComplete = 2;

        private static int _runLoopSequence;
        private static int _disposeSequence;
        private static int _activeGpuThreadCount;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private int _runLoopState;
        private int _stopRequested;
        private int _productionSlowPathRequired;
        private int _disposeState;
        private int _runLoopId;
        private ExceptionDispatchInfo _renderLoopException;
        private readonly object _disposeLock = new();

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private int _commandCount;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr;

        private int _refProducerPtr;
        private int _refConsumerPtr;

        private Action _interruptAction;
        private readonly object _interruptLock = new();

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal BufferMap Buffers { get; }
        internal SyncMap Sync { get; }
        internal CircularSpanPool SpanPool { get; }
        internal ProgramQueue Programs { get; }

        public IPipeline Pipeline { get; }
        public IWindow Window { get; }

        public IRenderer BaseRenderer => _baseRenderer;

        public static int ActiveGpuThreadCount => Volatile.Read(ref _activeGpuThreadCount);

        public bool PreferThreading => _baseRenderer.PreferThreading;

        public ThreadedRenderer(IRenderer renderer)
        {
            _baseRenderer = renderer;

            renderer.ScreenCaptured += (sender, info) => ScreenCaptured?.Invoke(this, info);
            renderer.SetInterruptAction(Interrupt);

            Pipeline = new ThreadedPipeline(this);
            Window = new ThreadedWindow(this, renderer);
            Buffers = new BufferMap();
            Sync = new SyncMap();
            Programs = new ProgramQueue(renderer);

            _galWorkAvailable = new ManualResetEventSlim(false);
            _invokeRun = new ManualResetEventSlim();
            _interruptRun = new AutoResetEvent(false);
            _spanPool = new CircularSpanPool(this, SpanPoolBytes);
            SpanPool = _spanPool;

            _elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);

            _commandQueue = new byte[_elementSize * QueueCount];
            _refQueue = new object[MaxRefsPerCommand * QueueCount];
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            ArgumentNullException.ThrowIfNull(gpuLoop);

            lock (_disposeLock)
            {
                ThrowIfDisposed();

                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    throw new InvalidOperationException("The threaded renderer has already been requested to stop.");
                }

                if (Interlocked.CompareExchange(ref _runLoopState, RunLoopRunning, RunLoopNotStarted) != RunLoopNotStarted)
                {
                    throw new InvalidOperationException("The threaded renderer run loop can only be started once.");
                }

                _backendThread = Thread.CurrentThread;
                _runLoopId = Interlocked.Increment(ref _runLoopSequence);

                _gpuThread = new Thread(() => RunGpuLoop(gpuLoop))
                {
                    Name = "GPU.MainThread",
                };

                Logger.Debug?.Print(
                    LogClass.Gpu,
                    $"Threaded renderer RunLoop #{_runLoopId} starting on managed thread {Environment.CurrentManagedThreadId}.");

                try
                {
                    _gpuThread.Start();
                }
                catch
                {
                    Volatile.Write(ref _runLoopState, RunLoopExited);
                    SignalWaiters();
                    throw;
                }
            }

            RenderLoop();
        }

        private void RunGpuLoop(ThreadStart gpuLoop)
        {
            Interlocked.Increment(ref _activeGpuThreadCount);

            try
            {
                gpuLoop();
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _stopRequested) != 0)
            {
            }
            catch (Exception exception)
            {
                RecordRenderLoopException(exception);
                RequestStopAfterDrain();
                SignalWaiters();
            }
            finally
            {
                Interlocked.Decrement(ref _activeGpuThreadCount);
            }
        }

        public void RenderLoop()
        {
            if (Thread.CurrentThread != _backendThread || Volatile.Read(ref _runLoopState) != RunLoopRunning)
            {
                throw new InvalidOperationException("The render loop must run on the renderer backend thread.");
            }

            try
            {
                while (true)
                {
                    _galWorkAvailable.Wait();
                    _galWorkAvailable.Reset();

                    DrainPendingWorkOnBackendThread();

                    if (Volatile.Read(ref _stopRequested) != 0 &&
                        Volatile.Read(ref _commandCount) == 0 &&
                        Volatile.Read(ref _interruptAction) == null)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                RecordRenderLoopException(exception);
            }
            finally
            {
                Volatile.Write(ref _runLoopState, RunLoopExited);
                SignalWaiters();

                Logger.Debug?.Print(
                    LogClass.Gpu,
                    $"Threaded renderer RunLoop #{_runLoopId} exited on managed thread {Environment.CurrentManagedThreadId}.");
            }

            ThrowIfRenderLoopFaulted();
        }

        private void DrainPendingWorkOnBackendThread()
        {
            Debug.Assert(Thread.CurrentThread == _backendThread);

            while (true)
            {
                ProcessInterruptAction();

                while (Volatile.Read(ref _commandCount) > 0 && Volatile.Read(ref _interruptAction) == null)
                {
                    int commandPtr = _consumerPtr;

                    Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                    try
                    {
                        CommandHelper.RunCommand(command, this, _baseRenderer);
                    }
                    finally
                    {
                        if (Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                        {
                            _invokeRun.Set();
                        }

                        _consumerPtr = (_consumerPtr + 1) % QueueCount;

                        Interlocked.Decrement(ref _commandCount);
                    }
                }

                if (Volatile.Read(ref _interruptAction) == null)
                {
                    break;
                }
            }
        }

        private void ProcessInterruptAction()
        {
            Action action = Volatile.Read(ref _interruptAction);

            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception exception)
            {
                RecordRenderLoopException(exception);
                throw;
            }
            finally
            {
                Interlocked.CompareExchange(ref _interruptAction, null, action);
                _interruptRun.Set();
            }
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            return _spanPool.Insert(data);
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref T New<T>() where T : struct, IGALCommand
        {
            // Command creation is a GPU hot path. Healthy sessions pay one
            // volatile read; detailed thread/fault checks run only during stop,
            // disposal or a backend fault.
            if (Volatile.Read(ref _productionSlowPathRequired) != 0)
            {
                ThrowIfCommandProductionUnavailable();
            }

            while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                // If incrementing the producer pointer would overflow, we need to wait.
                // _consumerPtr can only move forward, so there's no race to worry about here.

                ThrowIfCommandProductionUnavailable();

                if (Thread.CurrentThread == _backendThread && Volatile.Read(ref _runLoopState) != RunLoopRunning)
                {
                    DrainPendingWorkOnBackendThread();
                    continue;
                }

                ThrowIfCommandConsumerUnavailable();
                Thread.Sleep(1);
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) % QueueCount;

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            // The generic interface constraint emits a constrained call for these
            // command structs. Avoid an explicit interface cast here: it boxed one
            // object for every produced GAL command.
            memory[^1] = (byte)result.CommandType;

            return ref result;
        }

        internal int AddTableRef(object obj)
        {
            // The reference table is sized so that it will never overflow, so long as the references are taken after the command is allocated.

            int index = _refProducerPtr;

            _refQueue[index] = obj;

            _refProducerPtr = (_refProducerPtr + 1) % _refQueue.Length;

            return index;
        }

        internal object RemoveTableRef(int index)
        {
            Debug.Assert(index == _refConsumerPtr);

            object result = _refQueue[_refConsumerPtr];
            _refQueue[_refConsumerPtr] = null;

            _refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;

            return result;
        }

        internal void QueueCommand()
        {
            int result = Interlocked.Increment(ref _commandCount);

            if (result == 1)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            _invokeRun.Reset();
            _invokePtr = _lastProducedPtr;

            QueueCommand();

            if (IsBackendThread())
            {
                DrainPendingWorkOnBackendThread();

                if (Volatile.Read(ref _runLoopState) == RunLoopRunning)
                {
                    ThrowIfRenderLoopFaulted();
                }

                return;
            }

            // Wait for the command to complete.
            while (Volatile.Read(ref _invokePtr) != -1)
            {
                _invokeRun.Wait(10);

                if (Volatile.Read(ref _invokePtr) != -1)
                {
                    ThrowIfCommandConsumerUnavailable();
                }
            }

            ThrowIfRenderLoopFaulted();
            ThrowIfDisposed();
        }

        internal void WaitForFrame()
        {
            while (!_frameComplete.WaitOne(10))
            {
                ThrowIfCommandConsumerUnavailable();
            }

            ThrowIfCommandConsumerUnavailable();
        }

        internal void SignalFrame()
        {
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            return Thread.CurrentThread == _gpuThread;
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                // The action must be performed on the render thread.
                New<ActionCommand>().Set(Ref(action));
                InvokeCommand();
            }
            else
            {
                _baseRenderer.BackgroundContextAction(action, true);
            }
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>().Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>().Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>().Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            var imageArray = new ThreadedImageArray(this);
            New<CreateImageArrayCommand>().Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            var program = new ThreadedProgram(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            var sampler = new ThreadedSampler(this);
            New<CreateSamplerCommand>().Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public void CreateSync(ulong id, bool strict)
        {
            Sync.CreateSyncHandle(id);

            try
            {
                New<CreateSyncCommand>().Set(id, strict);
                QueueCommand();
            }
            catch
            {
                Sync.AssignSync(id);
                throw;
            }
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (IsGpuThread())
            {
                var texture = new ThreadedTexture(this, info);
                New<CreateTextureCommand>().Set(Ref(texture), info);
                QueueCommand();

                return texture;
            }
            else
            {
                var texture = new ThreadedTexture(this, info)
                {
                    Base = _baseRenderer.CreateTexture(info),
                };

                return texture;
            }
        }
        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            var textureArray = new ThreadedTextureArray(this);
            New<CreateTextureArrayCommand>().Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            New<BufferDisposeCommand>().Set(buffer);
            QueueCommand();
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (IsGpuThread())
            {
                ResultBox<PinnedSpan<byte>> box = new();
                New<BufferGetDataCommand>().Set(buffer, offset, size, Ref(box));
                InvokeCommand();

                return box.Result;
            }
            else
            {
                return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
            }
        }

        public Capabilities GetCapabilities()
        {
            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>().Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return _baseRenderer.GetHardwareInfo();
        }

        /// <summary>
        /// Initialize the base renderer. Must be called on the render thread.
        /// </summary>
        /// <param name="logLevel">Log level to use</param>
        public void Initialize(GraphicsDebugLevel logLevel)
        {
            _baseRenderer.Initialize(logLevel);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            var program = new ThreadedProgram(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public void PreFrame()
        {
            New<PreFrameCommand>();
            QueueCommand();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            ThreadedCounterEvent evt = new(this, type, _lastSampleCounterClear);
            New<ReportCounterCommand>().Set(Ref(evt), type, Ref(resultHandler), divisor, hostReserved);
            QueueCommand();

            if (type == CounterType.SamplesPassed)
            {
                _lastSampleCounterClear = false;
            }

            return evt;
        }

        public void ResetCounter(CounterType type)
        {
            New<ResetCounterCommand>().Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            _baseRenderer.Screenshot();
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            New<BufferSetDataCommand>().Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public void UpdateCounters()
        {
            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            Sync.WaitSyncAvailability(id, ThrowIfCommandConsumerUnavailable);

            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            // Interrupt the backend thread from any external thread and invoke the given action.

            if (Thread.CurrentThread == _backendThread)
            {
                // If this is called from the backend thread, the action can run immediately.
                action();
            }
            else
            {
                ThrowIfCommandConsumerUnavailable();

                lock (_interruptLock)
                {
                    while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
                    {
                        ThrowIfCommandConsumerUnavailable();
                        Thread.Yield();
                    }

                    _galWorkAvailable.Set();

                    while (Volatile.Read(ref _interruptAction) == action)
                    {
                        _interruptRun.WaitOne(10);
                        ThrowIfCommandConsumerUnavailable();
                    }

                    ThrowIfRenderLoopFaulted();
                }
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action, as it provides its own to the child renderer.
        }

        /// <summary>
        /// Requests that the backend run loop exits after all queued commands and interrupts have been processed.
        /// This does not dispose the renderer.
        /// </summary>
        public void RequestStopAfterDrain()
        {
            Volatile.Write(ref _productionSlowPathRequired, 1);
            Interlocked.Exchange(ref _stopRequested, 1);
            TrySet(_galWorkAvailable);
        }

        /// <summary>
        /// Waits for the GPU producer owned by this renderer to exit.
        /// The caller must request producer cancellation before waiting on a fault path.
        /// </summary>
        public void WaitForGpuThreadExit()
        {
            Thread gpuThread = Volatile.Read(ref _gpuThread);

            if (gpuThread != null && gpuThread != Thread.CurrentThread)
            {
                gpuThread.Join();
            }
        }

        /// <summary>
        /// Transfers command-consumer ownership to the current host teardown thread.
        /// The caller must serialize teardown phases and may only transfer ownership
        /// after the run loop has exited (or before it was started).
        /// </summary>
        public void TakeBackendThreadOwnership()
        {
            if (!TryTakeBackendThreadOwnership())
            {
                throw new InvalidOperationException(
                    "Backend ownership cannot be transferred while the renderer run loop is active.");
            }
        }

        /// <summary>
        /// Tries to transfer command-consumer ownership to the current host teardown
        /// thread. A running backend remains the owner and continues consuming commands.
        /// </summary>
        public bool TryTakeBackendThreadOwnership()
        {
            lock (_disposeLock)
            {
                ThrowIfDisposed();

                Thread currentThread = Thread.CurrentThread;

                if (currentThread == _gpuThread)
                {
                    throw new InvalidOperationException("The GPU producer cannot own the renderer backend.");
                }

                int runLoopState = Volatile.Read(ref _runLoopState);

                if (runLoopState == RunLoopRunning)
                {
                    return false;
                }

                _backendThread = currentThread;
                return true;
            }
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            if (Thread.CurrentThread == _backendThread)
            {
                DrainPendingWorkOnBackendThread();
                ThrowIfRenderLoopFaulted();

                return;
            }

            SpinWait wait = new();

            while (Volatile.Read(ref _commandCount) > 0)
            {
                ThrowIfCommandConsumerUnavailable();
                wait.SpinOnce();
            }

            ThrowIfRenderLoopFaulted();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            lock (_disposeLock)
            {
                if (Volatile.Read(ref _disposeState) != DisposeActive)
                {
                    return;
                }

                Thread currentThread = Thread.CurrentThread;
                int runLoopState = Volatile.Read(ref _runLoopState);

                if (currentThread == _gpuThread)
                {
                    throw new InvalidOperationException("The threaded renderer cannot be disposed from the GPU thread.");
                }

                if (runLoopState == RunLoopNotStarted && _backendThread == null)
                {
                    _backendThread = currentThread;
                }

                if (!IsBackendThread())
                {
                    throw new InvalidOperationException("The threaded renderer must be disposed from its backend thread.");
                }

                if (runLoopState == RunLoopRunning)
                {
                    throw new InvalidOperationException("Request the threaded renderer to stop and wait for RunLoop to return before disposing it.");
                }

                Volatile.Write(ref _disposeState, DisposeInProgress);

                int disposeId = Interlocked.Increment(ref _disposeSequence);

                Logger.Debug?.Print(
                    LogClass.Gpu,
                    $"Threaded renderer Dispose #{disposeId} starting on managed thread {Environment.CurrentManagedThreadId}.");

                // RunLoop already propagates producer/consumer faults. Dispose
                // reports only teardown failures so an old execution fault cannot
                // mask successful backend cleanup.
                ExceptionDispatchInfo firstException = null;

                try
                {
                    RequestStopAfterDrain();

                    if (_gpuThread != null && _gpuThread.IsAlive)
                    {
                        CaptureException(_gpuThread.Join, ref firstException);
                    }

                    DrainPendingWorkForDispose(ref firstException);
                    CaptureException(_baseRenderer.Dispose, ref firstException);

                    CaptureException(Sync.Dispose, ref firstException);
                    CaptureException(_frameComplete.Dispose, ref firstException);
                    CaptureException(_galWorkAvailable.Dispose, ref firstException);
                    CaptureException(_invokeRun.Dispose, ref firstException);
                    CaptureException(_interruptRun.Dispose, ref firstException);
                }
                finally
                {
                    Volatile.Write(ref _disposeState, DisposeComplete);

                    Logger.Debug?.Print(
                        LogClass.Gpu,
                        $"Threaded renderer Dispose #{disposeId} completed on managed thread {Environment.CurrentManagedThreadId}.");
                }

                firstException?.Throw();
            }
        }

        private void DrainPendingWorkForDispose(ref ExceptionDispatchInfo firstException)
        {
            while (Volatile.Read(ref _commandCount) > 0 ||
                   Volatile.Read(ref _interruptAction) != null)
            {
                int commandCount = Volatile.Read(ref _commandCount);
                int consumerPtr = Volatile.Read(ref _consumerPtr);
                Action interruptAction = Volatile.Read(ref _interruptAction);

                CaptureException(DrainPendingWorkOnBackendThread, ref firstException);

                if (commandCount == Volatile.Read(ref _commandCount) &&
                    consumerPtr == Volatile.Read(ref _consumerPtr) &&
                    ReferenceEquals(interruptAction, Volatile.Read(ref _interruptAction)))
                {
                    firstException ??= ExceptionDispatchInfo.Capture(
                        new InvalidOperationException("The threaded renderer dispose queue could not make progress."));
                    break;
                }
            }
        }

        private void ThrowIfCommandProductionUnavailable()
        {
            bool isBackendThread = IsBackendThread();

            if (!isBackendThread)
            {
                ThrowIfRenderLoopFaulted();
            }

            int disposeState = Volatile.Read(ref _disposeState);

            if (disposeState == DisposeComplete ||
                disposeState == DisposeInProgress && !isBackendThread)
            {
                throw new ObjectDisposedException(nameof(ThreadedRenderer));
            }

            if (!isBackendThread &&
                Volatile.Read(ref _stopRequested) != 0)
            {
                throw new OperationCanceledException("The threaded renderer is stopping and no longer accepts producer commands.");
            }

            if (!isBackendThread &&
                Volatile.Read(ref _runLoopState) == RunLoopExited)
            {
                throw new InvalidOperationException("The threaded renderer is no longer accepting commands.");
            }
        }

        private bool IsBackendThread()
        {
            return Thread.CurrentThread == Volatile.Read(ref _backendThread);
        }

        private void ThrowIfCommandConsumerUnavailable()
        {
            ThrowIfRenderLoopFaulted();
            ThrowIfDisposed();

            if (Volatile.Read(ref _runLoopState) != RunLoopRunning)
            {
                throw new InvalidOperationException("The threaded renderer run loop is not available.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeState) != DisposeActive)
            {
                throw new ObjectDisposedException(nameof(ThreadedRenderer));
            }
        }

        private void ThrowIfRenderLoopFaulted()
        {
            Volatile.Read(ref _renderLoopException)?.Throw();
        }

        private void RecordRenderLoopException(Exception exception)
        {
            Volatile.Write(ref _productionSlowPathRequired, 1);
            Interlocked.CompareExchange(
                ref _renderLoopException,
                ExceptionDispatchInfo.Capture(exception),
                null);
        }

        private void SignalWaiters()
        {
            TrySet(_galWorkAvailable);
            TrySet(_invokeRun);
            TrySet(_frameComplete);
            TrySet(_interruptRun);
            Sync.SignalWaiters();
        }

        private static void TrySet(ManualResetEventSlim waitHandle)
        {
            try
            {
                waitHandle.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void TrySet(EventWaitHandle waitHandle)
        {
            try
            {
                waitHandle.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void CaptureException(Action action, ref ExceptionDispatchInfo firstException)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
