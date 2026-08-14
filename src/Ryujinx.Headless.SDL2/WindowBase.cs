using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.SDL2.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static SDL2.SDL;
using AntiAliasing = Ryujinx.Common.Configuration.AntiAliasing;
using ScalingFilter = Ryujinx.Common.Configuration.ScalingFilter;
using Switch = Ryujinx.HLE.Switch;

namespace Ryujinx.Headless.SDL2
{
    internal sealed class WindowConstructionRollbackException : AggregateException
    {
        public WindowConstructionRollbackException(
            Exception constructionFailure,
            IEnumerable<Exception> rollbackFailures)
            : base(
                "Window construction failed and its rollback did not complete.",
                new[] { constructionFailure }.Concat(rollbackFailures))
        {
        }
    }

    abstract partial class WindowBase : IHostUIHandler, IDisposable
    {
        protected const int DefaultWidth = 1280;
        protected const int DefaultHeight = 720;
        private const int TargetFps = 60;
        private SDL_WindowFlags DefaultFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS | SDL_WindowFlags.SDL_WINDOW_SHOWN;
        private SDL_WindowFlags FullscreenFlag = 0;

        private static readonly ConcurrentQueue<Action> _mainThreadActions = new();
        private static readonly object _mainThreadActionsLock = new();
        private static bool _mainThreadActionsEnabled;

        internal static int ActiveRenderThreadCount;

        [LibraryImport("SDL2.framework/SDL2")]
        // TODO: Remove this as soon as SDL2-CS was updated to expose this method publicly
        private static partial IntPtr SDL_LoadBMP_RW(IntPtr src, int freesrc);

        public static bool TryQueueMainThreadAction(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_mainThreadActionsLock)
            {
                if (!_mainThreadActionsEnabled)
                {
                    return false;
                }

                _mainThreadActions.Enqueue(action);
                return true;
            }
        }

        public bool _isPaused;
        public ManualResetEvent _pauseEvent;

        public NpadManager NpadManager;
        public TouchScreenManager TouchScreenManager;
        public Switch Device;
        public IRenderer Renderer;

        public event EventHandler<StatusUpdatedEventArgs> StatusUpdatedEvent;

        public IntPtr WindowHandle;

        public IHostUITheme HostUITheme { get; }
        public static int Width { get; private set; }
        public static int Height { get; private set; }
        public int DisplayId { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsExclusiveFullscreen { get; set; }
        public int ExclusiveFullscreenWidth { get; set; }
        public int ExclusiveFullscreenHeight { get; set; }
        public AntiAliasing AntiAliasing { get; set; }
        public ScalingFilter ScalingFilter { get; set; }
        public int ScalingFilterLevel { get; set; }

        public iOSTouchDriver MouseDriver;
        private readonly InputManager _inputManager;
        private readonly IKeyboard _keyboardInterface;
        private readonly GraphicsDebugLevel _glLogLevel;
        private readonly Stopwatch _chrono;
        private readonly long _ticksPerFrame;
        private readonly CancellationTokenSource _gpuCancellationTokenSource;
        private Task _gpuCancellationTask = Task.CompletedTask;
        private readonly ManualResetEvent _gpuDoneEvent;
        private readonly ManualResetEvent _rendererFinalizeAuthorizedEvent;

        private long _ticks;
        private volatile bool _isActive;
        private int _stopRequested;
        private int _gpuDoneSignaled;
        private int _rendererStopRequested;
        private int _rendererFinalized;
        private int _rendererRunLoopEntered;
        private int _rendererRunLoopExited;
        private int _renderLoopStarted;
        private int _disposeState;
        private Thread _renderLoopThread;
        private ExceptionDispatchInfo _renderFailure;
        private ExceptionDispatchInfo _rendererDisposeFailure;
        private uint _windowId;

        private string _gpuDriverName;

        public AspectRatio _aspectRatio;
        private readonly bool _enableMouse;

        public WindowBase(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode)
        {
            _inputManager = inputManager ?? throw new ArgumentNullException(nameof(inputManager));
            _glLogLevel = glLogLevel;
            _chrono = new Stopwatch();
            _ticksPerFrame = Stopwatch.Frequency / TargetFps;
            _gpuCancellationTokenSource = new CancellationTokenSource();
            _gpuDoneEvent = new ManualResetEvent(false);
            _rendererFinalizeAuthorizedEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _aspectRatio = aspectRatio;
            _enableMouse = enableMouse;
            HostUITheme = new HeadlessHostUiTheme();

            bool mouseAttached = false;
            bool sdlReferenceAcquired = false;

            try
            {
                MouseDriver = new iOSTouchDriver(hideCursorMode);
                _inputManager.SetMouseDriver(MouseDriver);
                mouseAttached = true;
                NpadManager = _inputManager.CreateNpadManager();
                TouchScreenManager = _inputManager.CreateTouchScreenManager();
                _keyboardInterface = (IKeyboard)_inputManager.KeyboardDriver.GetGamepad("0");

                // The iOS SDL driver is process-owned by InputManager. MoltenVK does not
                // create an SDL window, so taking a per-window reference would never be
                // balanced by WindowBase.Dispose.
                if (!OperatingSystem.IsIOS())
                {
                    SDL2Driver.Instance.Initialize();
                    sdlReferenceAcquired = true;
                }
            }
            catch (Exception constructionFailure)
            {
                List<Exception> rollbackFailures = new();

                RollbackWindowConstruction(
                    () =>
                    {
                        if (sdlReferenceAcquired)
                        {
                            SDL2Driver.Instance.Dispose();
                        }
                    },
                    rollbackFailures);
                RollbackWindowConstruction(() => TouchScreenManager?.Dispose(), rollbackFailures);
                RollbackWindowConstruction(() => NpadManager?.Dispose(), rollbackFailures);
                RollbackWindowConstruction(
                    () =>
                    {
                        if (mouseAttached)
                        {
                            _inputManager.SetMouseDriver(null);
                        }
                        else
                        {
                            MouseDriver?.Dispose();
                        }
                    },
                    rollbackFailures);
                RollbackWindowConstruction(_gpuCancellationTokenSource.Dispose, rollbackFailures);
                RollbackWindowConstruction(_gpuDoneEvent.Dispose, rollbackFailures);
                RollbackWindowConstruction(_rendererFinalizeAuthorizedEvent.Dispose, rollbackFailures);
                RollbackWindowConstruction(_pauseEvent.Dispose, rollbackFailures);

                if (rollbackFailures.Count != 0)
                {
                    throw new WindowConstructionRollbackException(
                        constructionFailure,
                        rollbackFailures);
                }

                throw;
            }
        }

        private static void RollbackWindowConstruction(
            Action cleanup,
            List<Exception> rollbackFailures)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                rollbackFailures.Add(exception);
            }
        }

        public void Initialize(Switch device, List<InputConfig> inputConfigs, bool enableKeyboard, bool enableMouse)
        {
            Device = device;

            IRenderer renderer = Device.Gpu.Renderer;

            if (renderer is ThreadedRenderer tr)
            {
                renderer = tr.BaseRenderer;
            }

            Renderer = renderer;

            NpadManager.Initialize(device, inputConfigs, enableKeyboard, enableMouse);
            TouchScreenManager.Initialize(device);
        }

        private void SetWindowIcon()
        {
            Stream iconStream = typeof(WindowBase).Assembly.GetManifestResourceStream("Ryujinx.Headless.SDL2.Ryujinx.bmp");
            byte[] iconBytes = new byte[iconStream!.Length];

            if (iconStream.Read(iconBytes, 0, iconBytes.Length) != iconBytes.Length)
            {
                Logger.Error?.Print(LogClass.Application, "Failed to read icon to byte array.");
                iconStream.Close();

                return;
            }

            iconStream.Close();

            unsafe
            {
                fixed (byte* iconPtr = iconBytes)
                {
                    IntPtr rwOpsStruct = SDL_RWFromConstMem((IntPtr)iconPtr, iconBytes.Length);
                    IntPtr iconHandle = SDL_LoadBMP_RW(rwOpsStruct, 1);

                    SDL_SetWindowIcon(WindowHandle, iconHandle);
                    SDL_FreeSurface(iconHandle);
                }
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "set_view_size")]
        static unsafe void set_view_size(int width, int height)
        {
            Width = width;
            Height = height;
             
        }

        private void InitializeWindow()
        {
            if (this is Vulkan.MoltenVKWindow) {
                Width = ExclusiveFullscreenWidth;
                Height = ExclusiveFullscreenHeight;

                MouseDriver.SetClientSize(Width, Height);
                return;
            } 

            var activeProcess = Device.Processes.ActiveApplication;
            var nacp = activeProcess.ApplicationControlProperties;
            int desiredLanguage = (int)Device.System.State.DesiredTitleLanguage;

            string titleNameSection = string.IsNullOrWhiteSpace(nacp.Title[desiredLanguage].NameString.ToString()) ? string.Empty : $" - {nacp.Title[desiredLanguage].NameString.ToString()}";
            string titleVersionSection = string.IsNullOrWhiteSpace(nacp.DisplayVersionString.ToString()) ? string.Empty : $" v{nacp.DisplayVersionString.ToString()}";
            string titleIdSection = string.IsNullOrWhiteSpace(activeProcess.ProgramIdText) ? string.Empty : $" ({activeProcess.ProgramIdText.ToUpper()})";
            string titleArchSection = activeProcess.Is64Bit ? " (64-bit)" : " (32-bit)";

            Width = DefaultWidth;
            Height = DefaultHeight;

            if (IsExclusiveFullscreen)
            {
                Width = ExclusiveFullscreenWidth;
                Height = ExclusiveFullscreenHeight;

                DefaultFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI;
                FullscreenFlag = SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
            }
            else if (IsFullscreen)
            {
                DefaultFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI;
                FullscreenFlag = SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP;
            }

            // WindowHandle = SDL_GetWindowFromID(1);
            WindowHandle = SDL_CreateWindow($"Ryujinx {Program.Version}{titleNameSection}{titleVersionSection}{titleIdSection}{titleArchSection}", 0, 0, Width, Height, DefaultFlags | FullscreenFlag | GetWindowFlags());

            if (WindowHandle == IntPtr.Zero)
            {
                string errorMessage = $"SDL_CreateWindow failed with error \"{SDL_GetError()}\"";

                Logger.Error?.Print(LogClass.Application, errorMessage);

                throw new Exception(errorMessage);
            }

            SetWindowIcon();

            _windowId = SDL_GetWindowID(WindowHandle);
            SDL2Driver.Instance.RegisterWindow(_windowId, HandleWindowEvent);
            
        }

        private void HandleWindowEvent(SDL_Event evnt)
        {
            if (evnt.type == SDL_EventType.SDL_WINDOWEVENT)
            {
                switch (evnt.window.windowEvent)
                {
                    case SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED:
                        // Unlike on Windows, this event fires on macOS when triggering fullscreen mode.
                        // And promptly crashes the process because `Renderer?.window.SetSize` is undefined.
                        // As we don't need this to fire in either case we can test for fullscreen.
                        if (!IsFullscreen && !IsExclusiveFullscreen)
                        {
                            Width = evnt.window.data1;
                            Height = evnt.window.data2;
                            if (Renderer?.Window != null)
                            {
                                Renderer.Window.SetSize(Width, Height);
                            }
                            MouseDriver.SetClientSize(Width, Height);
                        }
                        break;

                    case SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE:
                        Exit();
                        break;
                }
            }
            else
            {
                // MouseDriver.Update(evnt);
            }
        }

        protected abstract void InitializeWindowRenderer();

        protected abstract void InitializeRenderer();

        protected virtual void PrepareForGpuDispose() { }

        protected virtual void FinalizeWindowRenderer(bool gpuDisposeSucceeded) { }

        protected abstract void SwapBuffers();

        public abstract SDL_WindowFlags GetWindowFlags();

        private string GetGpuDriverName()
        {
            return Renderer.GetHardwareInfo().GpuDriver;
        }

        private void SetAntiAliasing()
        {
            Renderer?.Window.SetAntiAliasing((Graphics.GAL.AntiAliasing)AntiAliasing);
        }

        private void SetScalingFilter()
        {
            Renderer?.Window.SetScalingFilter((Graphics.GAL.ScalingFilter)ScalingFilter);
            Renderer?.Window.SetScalingFilterLevel(ScalingFilterLevel);
        }

        public void Render()
        {
            Interlocked.Increment(ref ActiveRenderThreadCount);

            try
            {
                RenderCore();
            }
            finally
            {
                Interlocked.Decrement(ref ActiveRenderThreadCount);
            }
        }

        private void RenderCore()
        {
            try
            {
                InitializeWindowRenderer();

                Device.Gpu.Renderer.Initialize(_glLogLevel);

                InitializeRenderer();

                SetAntiAliasing();

                SetScalingFilter();

                _gpuDriverName = GetGpuDriverName();

                bool firstFrame = true;

                Interlocked.Exchange(ref _rendererRunLoopEntered, 1);

                try
                {
                    Device.Gpu.Renderer.RunLoop(() =>
                    {
                        try
                        {
                            Device.Gpu.SetGpuThread();
                            Device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                            while (_isActive && !IsStopRequested)
                            {
                                _pauseEvent.WaitOne();

                                if (IsStopRequested)
                                {
                                    break;
                                }

                                _ticks += _chrono.ElapsedTicks;

                                _chrono.Restart();

                                if (Device.WaitFifo())
                                {
                                    Device.Statistics.RecordFifoStart();
                                    Device.ProcessFrame();
                                    Device.Statistics.RecordFifoEnd();
                                }

                                while (Device.ConsumeFrameAvailable())
                                {
                                    Device.PresentFrame(SwapBuffers);

                                    if (firstFrame)
                                    {
                                        firstFrame = false;
                                        Program.LogLifecycleMilestone("FirstFrame");
                                        Program.TriggerCallback("ran-first-frame");
                                    }
                                }

                                if (_ticks >= _ticksPerFrame)
                                {
                                    string dockedMode = Device.System.State.DockedMode ? "Docked" : "Handheld";
                                    float scale = GraphicsConfig.ResScale;
                                    if (scale != 1)
                                    {
                                        dockedMode += $" ({scale}x)";
                                    }

                                    StatusUpdatedEvent?.Invoke(this, new StatusUpdatedEventArgs(
                                        Device.EnableDeviceVsync,
                                        dockedMode,
                                        Device.Configuration.AspectRatio.ToText(),
                                        $"Game: {Device.Statistics.GetGameFrameRate():00.00} FPS ({Device.Statistics.GetGameFrameTime():00.00} ms)",
                                        $"FIFO: {Device.Statistics.GetFifoPercent():0.00} %",
                                        $"GPU: {_gpuDriverName}"));

                                    _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (IsStopRequested)
                        {
                        }
                        catch (Exception exception)
                        {
                            RecordRenderFailure(exception);
                            RequestStop();
                        }
                        finally
                        {
                            try
                            {
                                // Make sure all commands submitted by the GPU producer are
                                // consumed before the HLE teardown starts.
                                if (Device.Gpu.Renderer is ThreadedRenderer threaded)
                                {
                                    threaded.FlushThreadedCommands();
                                }
                            }
                            catch (Exception exception)
                            {
                                RecordRenderFailure(exception);
                            }
                            finally
                            {
                                SignalGpuDone();
                            }
                        }
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref _rendererRunLoopExited, 1);
                }
            }
            catch (OperationCanceledException) when (IsStopRequested)
            {
                WaitForGpuProducerExit();
                SignalGpuDone();
            }
            catch (Exception exception)
            {
                RecordRenderFailure(exception);
                RequestStop();
                WaitForGpuProducerExit();
                SignalGpuDone();
            }
            finally
            {
                // GPU disposal must happen after Switch.Dispose. The session
                // coordinator opens this gate once HLE teardown is complete.
                WaitForGpuProducerExit();
                SignalGpuDone();
                _rendererFinalizeAuthorizedEvent.WaitOne();

                try
                {
                    Program.LogLifecycleMilestone("GpuDisposeStart");
                    DisposeGpuOnRenderThread();
                    Program.LogLifecycleMilestone("GpuDisposed");
                }
                catch (Exception exception)
                {
                    Program.LogLifecycleMilestone("GpuDisposeFault");
                    RecordRendererDisposeFailure(exception);
                }
                finally
                {
                    Interlocked.Exchange(ref _rendererFinalized, 1);
                    SignalGpuDone();
                }
            }
        }

        private bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

        private void RecordRenderFailure(Exception exception)
        {
            Interlocked.CompareExchange(
                ref _renderFailure,
                ExceptionDispatchInfo.Capture(exception),
                null);

            if (exception is VulkanRendererInitializationRollbackException ||
                Volatile.Read(ref _gpuDoneSignaled) != 0)
            {
                RecordRendererDisposeFailure(exception);
            }
        }

        private void RecordRendererDisposeFailure(Exception exception)
        {
            Interlocked.CompareExchange(
                ref _rendererDisposeFailure,
                ExceptionDispatchInfo.Capture(exception),
                null);
        }

        private void DisposeGpuOnRenderThread()
        {
            ExceptionDispatchInfo failure = null;
            bool gpuDisposeSucceeded = false;

            RunDisposeStep(
                "renderer context preparation",
                PrepareForGpuDispose,
                ref failure);
            RunDisposeStep(
                "GPU context",
                () =>
                {
                    Device?.DisposeGpu();
                    gpuDisposeSucceeded =
                        failure == null &&
                        Volatile.Read(ref _rendererDisposeFailure) == null;
                },
                ref failure);
            RunDisposeStep(
                "window renderer finalization",
                () => FinalizeWindowRenderer(gpuDisposeSucceeded),
                ref failure);

            failure?.Throw();
        }

        private void WaitForGpuProducerExit()
        {
            try
            {
                if (Device?.Gpu?.Renderer is ThreadedRenderer threaded)
                {
                    threaded.WaitForGpuThreadExit();
                }
            }
            catch (Exception exception)
            {
                RecordRenderFailure(exception);
            }
        }

        private static void RunDisposeStep(string name, Action action, ref ExceptionDispatchInfo failure)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                if (failure == null)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                else
                {
                    Logger.Error?.Print(LogClass.Application, $"Additional failure while disposing {name}: {exception}");
                }
            }
        }

        private void SignalGpuDone()
        {
            if (Interlocked.Exchange(ref _gpuDoneSignaled, 1) == 0)
            {
                _gpuDoneEvent.Set();
            }
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                try
                {
                    Volatile.Write(ref _gpuCancellationTask, _gpuCancellationTokenSource.CancelAsync());
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _isActive = false;

            try
            {
                _pauseEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Exit()
        {
            RequestStop();
        }

        public static void ProcessMainThreadQueue()
        {
            while (_mainThreadActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        public void MainLoop()
        {
            while (_isActive && !IsStopRequested)
            {
                UpdateFrame();

                SDL_PumpEvents();

                ProcessMainThreadQueue();

                // Polling becomes expensive if it's not slept
                // Why was this commented out???
                Thread.Sleep(1); 
            }
        }

        private void NvidiaStutterWorkaround()
        {
            while (_isActive)
            {
                // When NVIDIA Threaded Optimization is on, the driver will snapshot all threads in the system whenever the application creates any new ones.
                // The ThreadPool has something called a "GateThread" which terminates itself after some inactivity.
                // However, it immediately starts up again, since the rules regarding when to terminate and when to start differ.
                // This creates a new thread every second or so.
                // The main problem with this is that the thread snapshot can take 70ms, is on the OpenGL thread and will delay rendering any graphics.
                // This is a little over budget on a frame time of 16ms, so creates a large stutter.
                // The solution is to keep the ThreadPool active so that it never has a reason to terminate the GateThread.

                // TODO: This should be removed when the issue with the GateThread is resolved.

                ThreadPool.QueueUserWorkItem(state => { });
                Thread.Sleep(300);
            }
        }

        private bool UpdateFrame()
        {
            if (!_isActive)
            {
                return true;
            }

            if (IsStopRequested)
            {
                return false;
            }

            NpadManager.Update();

            // Touchscreen
            bool hasTouch;

            MouseDriver.SetClientSize(Width, Height);

            hasTouch = TouchScreenManager.UpdateMultiTouch(_inputManager.MouseDriver.GetGamepad("") as ITouchScreen, true); // TouchScreenManager.Update(true, (_inputManager.MouseDriver as iOSTouchDriver).IsButtonPressed(MouseButton.Button1), (float)Width / Height);

            if (!hasTouch)
            {
                TouchScreenManager.Update(false);
            }

            Device.Hid.DebugPad.Update();

            // TODO: Replace this with MouseDriver.CheckIdle() when mouse motion events are received on every supported platform.
            MouseDriver.UpdatePosition();

            return true;
        }

        public void Execute()
        {
            if (IsStopRequested)
            {
                SignalGpuDone();
                return;
            }

            _chrono.Restart();
            _isActive = true;

            lock (_mainThreadActionsLock)
            {
                _mainThreadActionsEnabled = true;
            }

            try
            {
                InitializeWindow();

                if (IsStopRequested)
                {
                    SignalGpuDone();
                    return;
                }

                _renderLoopThread = new Thread(Render)
                {
                    Name = "GUI.RenderLoop",
                };
                _renderLoopThread.Start();
                Interlocked.Exchange(ref _renderLoopStarted, 1);

                MainLoop();

                // Wait for the GPU producer barrier while servicing SDL work that
                // was accepted before Stop closed the main loop.
                while (!_gpuDoneEvent.WaitOne(1))
                {
                    ProcessMainThreadQueue();
                }
            }
            finally
            {
                // Close the enqueue gate before the final drain. This prevents the
                // process-owned SDL worker from waiting on work left for a later session.
                lock (_mainThreadActionsLock)
                {
                    _mainThreadActionsEnabled = false;
                }

                ProcessMainThreadQueue();
            }
        }

        public void RequestRendererStopAfterDrain()
        {
            if (Interlocked.Exchange(ref _rendererStopRequested, 1) != 0)
            {
                return;
            }

            Thread renderLoopThread = Volatile.Read(ref _renderLoopThread);

            try
            {
                if (renderLoopThread == null || Volatile.Read(ref _renderLoopStarted) == 0)
                {
                    _renderLoopThread = new Thread(() =>
                    {
                        Interlocked.Increment(ref ActiveRenderThreadCount);

                        try
                        {
                            Program.LogLifecycleMilestone("GpuDisposeStart");
                            DisposeGpuOnRenderThread();
                            Program.LogLifecycleMilestone("GpuDisposed");
                        }
                        catch (Exception exception)
                        {
                            Program.LogLifecycleMilestone("GpuDisposeFault");
                            RecordRendererDisposeFailure(exception);
                        }
                        finally
                        {
                            try
                            {
                                Interlocked.Exchange(ref _rendererFinalized, 1);
                                SignalGpuDone();
                            }
                            finally
                            {
                                Interlocked.Decrement(ref ActiveRenderThreadCount);
                            }
                        }
                    })
                    {
                        Name = "GUI.RenderLoop.Cleanup",
                    };
                    _renderLoopThread.Start();
                    Interlocked.Exchange(ref _renderLoopStarted, 1);

                    return;
                }

                if (renderLoopThread.IsAlive &&
                    Volatile.Read(ref _rendererRunLoopEntered) != 0 &&
                    Volatile.Read(ref _rendererRunLoopExited) == 0)
                {
                    if (Device.Gpu.Renderer is not ThreadedRenderer threaded)
                    {
                        throw new InvalidOperationException("The headless renderer is expected to use ThreadedRenderer.");
                    }

                    threaded.RequestStopAfterDrain();
                }
            }
            finally
            {
                _rendererFinalizeAuthorizedEvent.Set();
            }
        }

        public void StopAndWaitForGpu()
        {
            RequestStop();

            if (Volatile.Read(ref _renderLoopStarted) != 0)
            {
                _gpuDoneEvent.WaitOne();
            }
        }

        public void WaitForRendererExit()
        {
            Thread renderLoopThread = Volatile.Read(ref _renderLoopThread);

            if (renderLoopThread != null && renderLoopThread != Thread.CurrentThread)
            {
                renderLoopThread.Join();
            }
        }

        public void ThrowIfRenderFailed()
        {
            Volatile.Read(ref _renderFailure)?.Throw();
        }

        public void ThrowIfRendererDisposeFailed()
        {
            Volatile.Read(ref _rendererDisposeFailure)?.Throw();
        }

        public void DisplayInputDialog(SoftwareKeyboardUIArgs args, Action<string> onTextEntered)
        {
            // SDL2 doesn't support input dialogs
            // Trying to use Objective-C on iDevices
            if (OperatingSystem.IsIOS())
            {
                AlertHelper.ShowAlertWithTextInput(args.HeaderText, args.SubtitleText, args.GuideText, (inputText) =>
                {
                    onTextEntered?.Invoke(inputText);
                });
            } else {
                onTextEntered?.Invoke("");
            }
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            if (OperatingSystem.IsIOS()) 
            {
                // TODO: Rework this :3
                Console.WriteLine($"Alert: {title}, message: {message}");
                //AlertHelper.ShowAlert(title, message, false);
            } else {
                SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_INFORMATION, title, message, WindowHandle);
            }

            return true;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            string playerCount = args.PlayerCountMin == args.PlayerCountMax ? $"exactly {args.PlayerCountMin}" : $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests {playerCount} player(s) with:\n\n"
                           + $"TYPES: {args.SupportedStyles}\n\n"
                           + $"PLAYERS: {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. Handheld is also invalid.\n\n" : "")
                           + "Please reconfigure Input now and then press OK.";

            return DisplayMessageDialog("Controller Applet", message);
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new HeadlessDynamicTextInputHandler();
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);

            Exit();
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            SDL_MessageBoxData data = new()
            {
                title = title,
                message = message,
                buttons = new SDL_MessageBoxButtonData[buttonsText.Length],
                numbuttons = buttonsText.Length,
                window = WindowHandle,
            };

            for (int i = 0; i < buttonsText.Length; i++)
            {
                data.buttons[i] = new SDL_MessageBoxButtonData
                {
                    buttonid = i,
                    text = buttonsText[i],
                };
            }

            SDL_ShowMessageBox(ref data, out int _);

            return true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            ExceptionDispatchInfo failure = null;

            RunDisposeStep("GPU producer", StopAndWaitForGpu, ref failure);

            if (Device != null && Volatile.Read(ref _rendererFinalized) == 0)
            {
                RunDisposeStep("renderer stop", RequestRendererStopAfterDrain, ref failure);
            }
            else
            {
                RunDisposeStep("renderer authorization", () => _rendererFinalizeAuthorizedEvent.Set(), ref failure);
            }

            RunDisposeStep("renderer thread", WaitForRendererExit, ref failure);
            RunDisposeStep("renderer GPU disposal", ThrowIfRendererDisposeFailed, ref failure);
            RunDisposeStep(
                "GPU cancellation callbacks",
                () => Volatile.Read(ref _gpuCancellationTask).GetAwaiter().GetResult(),
                ref failure);

            RunDisposeStep("touchscreen manager", () => TouchScreenManager?.Dispose(), ref failure);
            RunDisposeStep("npad manager", () => NpadManager?.Dispose(), ref failure);
            RunDisposeStep("mouse driver", () => _inputManager.SetMouseDriver(null), ref failure);

            RunDisposeStep("GPU cancellation source", _gpuCancellationTokenSource.Dispose, ref failure);
            RunDisposeStep("GPU done event", _gpuDoneEvent.Dispose, ref failure);
            RunDisposeStep("renderer authorization event", _rendererFinalizeAuthorizedEvent.Dispose, ref failure);
            RunDisposeStep("pause event", _pauseEvent.Dispose, ref failure);

            if (this is not Vulkan.MoltenVKWindow)
            {
                RunDisposeStep("SDL window registration", () => SDL2Driver.Instance.UnregisterWindow(_windowId), ref failure);
                RunDisposeStep("SDL window", () => SDL_DestroyWindow(WindowHandle), ref failure);
                RunDisposeStep("SDL driver reference", SDL2Driver.Instance.Dispose, ref failure);
            }

            StatusUpdatedEvent = null;
            TouchScreenManager = null;
            NpadManager = null;
            Renderer = null;
            Device = null;
            WindowHandle = IntPtr.Zero;
            _renderLoopThread = null;

            failure?.Throw();
        }
    }
}
