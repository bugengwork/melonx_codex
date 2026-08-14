using CommandLine;
using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.SDL2;
using Ryujinx.Audio.Backends.Apple;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.Common.GraphicsDriver;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.Utilities;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Headless.SDL2.OpenGL;
using Ryujinx.Headless.SDL2.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL2;
using Ryujinx.Memory;
using Ryujinx.SDL2.Common;
using Ryujinx.UI.Common.Configuration.System;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ConfigGamepadInputId = Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId;
using ConfigStickInputId = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;
using Key = Ryujinx.Common.Configuration.Hid.Key;
using Ryujinx.HLE.HOS.SystemState;
using LibHac.Common.Keys;
using LibHac.Common;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Fs;
using Path = System.IO.Path;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.HLE.Loaders.Npdm;
using System.Globalization;
using System.Text;
using LibHac.Ncm;
using Microsoft.Win32.SafeHandles;
using System.Text.RegularExpressions;
using System.Runtime;
using System.Runtime.ExceptionServices;
using System.Linq;
using System.Threading.Tasks;
using Ryujinx.Input.Native;

namespace Ryujinx.Headless.SDL2
{
    internal enum EmulationLifecycleDiagnosticField
    {
        SchemaVersion = 0,
        CurrentSessionId = 1,
        CurrentSessionState = 2,
        CurrentMilestoneMask = 3,
        LastSessionId = 4,
        LastSessionState = 5,
        LastMilestoneMask = 6,
        LastOrderViolationCount = 7,
        LastTeardownFailed = 8,
        LastResultCode = 9,
        LastPhysicalFootprint = 10,
        ActiveGpuThreadCount = 11,
        ActiveRenderThreadCount = 12,
        VulkanInstanceCreatedCount = 13,
        VulkanInstanceDestroyedCount = 14,
        VulkanSurfaceCreatedCount = 15,
        VulkanSurfaceDestroyedCount = 16,
        VulkanDeviceCreatedCount = 17,
        VulkanDeviceDestroyedCount = 18,
        JitMappingCreatedCount = 19,
        RequiredTeardownMilestoneMask = 20,
        VulkanValidationErrorCount = 21,
        VulkanDeviceLostCount = 22,
    }

    class Program
    {
        private const int TeardownIncompleteResultCode = -2;

        public static string Version { get; private set; }

        private static VirtualFileSystem _virtualFileSystem;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static InputManager _inputManager;
        private static EmulationSession _currentSession;
        private static EmulationLifecycleDiagnostics _lastLifecycleDiagnostics;
        private static long _nextSessionId;
        private static int _startPending;
        private static int _pendingStopRequest;
        private static int _unhandledExceptionHandlerRegistered;
        private static int _mainThreadDispatcherRegistered;
        private static WindowsMultimediaTimerResolution _windowsMultimediaTimerResolution;
        private static List<InputConfig> _inputConfiguration;
        private static bool _enableKeyboard;
        private static bool _enableMouse;
        private static IntPtr _pendingNativeMetalLayer = IntPtr.Zero;
        private static readonly object metalLayerLock = new object();
        private static readonly object inputManagerLock = new object();
        private static readonly object horizonManagerLock = new object();
        private static ExceptionDispatchInfo _processInitializationFailure;

        private static readonly InputConfigJsonSerializerContext _serializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        private static readonly TitleUpdateMetadataJsonSerializerContext _titleSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());

                
        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TriggerCallbackWithData(string cIdentifier, IntPtr data,  UIntPtr dataLength);

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TriggerCallback(string cIdentifier);

        [UnmanagedCallersOnly(EntryPoint = "main_ryujinx_sdl")]
        public static unsafe int MainExternal(int argCount, IntPtr* pArgs)
        {
            try
            {
                return MainExternalCore(argCount, pArgs);
            }
            catch (Exception exception)
            {
                LogAbiBoundaryFailure("main_ryujinx_sdl", exception);
                return TeardownIncompleteResultCode;
            }
        }

        private static unsafe int MainExternalCore(int argCount, IntPtr* pArgs)
        {
            EmulationSession session = new(Interlocked.Increment(ref _nextSessionId));
            bool sessionInstalled;
            int result = 0;

            lock (metalLayerLock)
            {
                sessionInstalled = Interlocked.CompareExchange(ref _currentSession, session, null) == null;

                if (sessionInstalled)
                {
                    session.SetNativeMetalLayer(_pendingNativeMetalLayer);
                    _pendingNativeMetalLayer = IntPtr.Zero;
                }
            }

            if (!sessionInstalled)
            {
                Volatile.Write(ref _startPending, 0);
                Interlocked.Exchange(ref _pendingStopRequest, 0);
                Console.WriteLine("An emulation session is already active.");

                return -1;
            }

            Volatile.Write(ref _startPending, 0);

            if (Interlocked.Exchange(ref _pendingStopRequest, 0) != 0)
            {
                session.RequestStop();
            }

            try
            {
                Volatile.Read(ref _processInitializationFailure)?.Throw();

                string[] args = new string[argCount];

                for (int i = 0; i < argCount; i++)
                {
                    args[i] = Marshal.PtrToStringAnsi(pArgs[i]);

                    Console.WriteLine(args[i]);
                }

                Main(args);
            }
            catch (Exception e)
            {
                session.RecordFault(e);
                Console.WriteLine(e.ToString());
                result = -1;
            }
            finally
            {
                if ((int)session.State < (int)EmulationSessionState.GpuDisposing)
                {
                    try
                    {
                        // Argument parsing and pre-load validation can return before
                        // a Window/Switch exists. They still need to cross the same
                        // coordinator boundary before the native entrypoint returns.
                        ShutdownEmulationInstance(session, null, null, null);
                    }
                    catch (Exception exception)
                    {
                        session.RecordTeardownFault(exception);
                        Console.WriteLine(exception.ToString());
                    }
                }

                _inputConfiguration = null;
                UserChannelPersistence userChannelPersistence =
                    Interlocked.Exchange(ref _userChannelPersistence, null);
                userChannelPersistence?.Clear();

                lock (metalLayerLock)
                {
                    if (!session.HasTeardownFailure && !session.MarkCompleted())
                    {
                        session.RecordTeardownFault(
                            new InvalidOperationException(
                                $"Emulation session {session.Id} did not reach the GPU teardown boundary."));
                    }

                    if (session.HasTeardownFailure)
                    {
                        session.LogMilestone("TeardownIncomplete");
                        result = TeardownIncompleteResultCode;
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _currentSession, null, session);
                    }

                    Volatile.Write(
                        ref _lastLifecycleDiagnostics,
                        session.CreateDiagnosticsSnapshot(result));
                }

                Interlocked.Exchange(ref _pendingStopRequest, 0);
            }

            return result;
        }

        [UnmanagedCallersOnly(EntryPoint = "set_native_window")]
        public static unsafe void SetNativeWindow(IntPtr layer)
        {
            try
            {
                lock (metalLayerLock)
                {
                    EmulationSession session = Volatile.Read(ref _currentSession);

                    if (session != null)
                    {
                        session.SetNativeMetalLayer(layer);
                    }
                    else
                    {
                        _pendingNativeMetalLayer = layer;
                    }
                }

                Logger.Info?.Print(LogClass.Application, $"SetNativeWindow called with layer: {layer}");
            }
            catch (Exception exception)
            {
                // Exceptions must not cross the unmanaged ABI boundary.
                Logger.Error?.Print(LogClass.Application, $"Failed to set native window: {exception}");
            }
        }

        internal static void LogLifecycleMilestone(string milestone)
        {
            Volatile.Read(ref _currentSession)?.LogMilestone(milestone);
        }

        [UnmanagedCallersOnly(EntryPoint = "get_emulation_lifecycle_diagnostic")]
        public static long GetEmulationLifecycleDiagnostic(int field)
        {
            try
            {
                EmulationSession currentSession = Volatile.Read(ref _currentSession);
                EmulationLifecycleDiagnostics last = Volatile.Read(ref _lastLifecycleDiagnostics);

                return (EmulationLifecycleDiagnosticField)field switch
                {
                    EmulationLifecycleDiagnosticField.SchemaVersion =>
                        EmulationLifecycleDiagnostics.SchemaVersion,
                    EmulationLifecycleDiagnosticField.CurrentSessionId =>
                        currentSession?.Id ?? 0,
                    EmulationLifecycleDiagnosticField.CurrentSessionState =>
                        currentSession != null ? (long)currentSession.State : -1,
                    EmulationLifecycleDiagnosticField.CurrentMilestoneMask =>
                        currentSession?.GetLifecycleMilestoneMask() ?? 0,
                    EmulationLifecycleDiagnosticField.LastSessionId =>
                        last?.SessionId ?? 0,
                    EmulationLifecycleDiagnosticField.LastSessionState =>
                        last?.State ?? -1,
                    EmulationLifecycleDiagnosticField.LastMilestoneMask =>
                        last?.MilestoneMask ?? 0,
                    EmulationLifecycleDiagnosticField.LastOrderViolationCount =>
                        last?.OrderViolationCount ?? 0,
                    EmulationLifecycleDiagnosticField.LastTeardownFailed =>
                        last?.TeardownFailed ?? 0,
                    EmulationLifecycleDiagnosticField.LastResultCode =>
                        last?.ResultCode ?? 0,
                    EmulationLifecycleDiagnosticField.LastPhysicalFootprint =>
                        last?.PhysicalFootprint ?? -1,
                    EmulationLifecycleDiagnosticField.ActiveGpuThreadCount =>
                        ThreadedRenderer.ActiveGpuThreadCount,
                    EmulationLifecycleDiagnosticField.ActiveRenderThreadCount =>
                        Volatile.Read(ref WindowBase.ActiveRenderThreadCount),
                    EmulationLifecycleDiagnosticField.VulkanInstanceCreatedCount =>
                        VulkanRenderer.InstanceCreatedCount,
                    EmulationLifecycleDiagnosticField.VulkanInstanceDestroyedCount =>
                        VulkanRenderer.InstanceDestroyedCount,
                    EmulationLifecycleDiagnosticField.VulkanSurfaceCreatedCount =>
                        VulkanRenderer.SurfaceCreatedCount,
                    EmulationLifecycleDiagnosticField.VulkanSurfaceDestroyedCount =>
                        VulkanRenderer.SurfaceDestroyedCount,
                    EmulationLifecycleDiagnosticField.VulkanDeviceCreatedCount =>
                        VulkanRenderer.DeviceCreatedCount,
                    EmulationLifecycleDiagnosticField.VulkanDeviceDestroyedCount =>
                        VulkanRenderer.DeviceDestroyedCount,
                    EmulationLifecycleDiagnosticField.JitMappingCreatedCount =>
                        DualMappedJitAllocator.SuccessfulMappingCount,
                    EmulationLifecycleDiagnosticField.RequiredTeardownMilestoneMask =>
                        last?.RequiredMilestoneMask ??
                            EmulationLifecycleDiagnostics.RequiredTeardownMilestones,
                    EmulationLifecycleDiagnosticField.VulkanValidationErrorCount =>
                        VulkanRenderer.ValidationErrorCount,
                    EmulationLifecycleDiagnosticField.VulkanDeviceLostCount =>
                        VulkanRenderer.DeviceLostCount,
                    _ => long.MinValue,
                };
            }
            catch
            {
                // Diagnostics are read-only and must never throw across the C ABI.
                return long.MinValue;
            }
        }


        [UnmanagedCallersOnly(EntryPoint = "create_account")]
        public static void CreateAccount(IntPtr namePtr, IntPtr imagePtr, int imageLength)
        {
            string name = Marshal.PtrToStringAnsi(namePtr);

            byte[] image = null;
            if (imagePtr != IntPtr.Zero && imageLength > 0)
            {
                image = new byte[imageLength];
                Marshal.Copy(imagePtr, image, 0, imageLength);
            }

            _accountManager.AddUser(name, image);
        }

        [UnmanagedCallersOnly(EntryPoint = "delete_account")]
        public static void DeleteAccount(IntPtr userId)
        {
            string name = Marshal.PtrToStringAnsi(userId);

            HLE.HOS.Services.Account.Acc.UserId userIdObj = new HLE.HOS.Services.Account.Acc.UserId(name);
            _accountManager.DeleteUser(userIdObj);
        }

        [UnmanagedCallersOnly(EntryPoint = "open_user")]
        public static void OpenUser(IntPtr userId)
        {
            string name = Marshal.PtrToStringAnsi(userId);

            HLE.HOS.Services.Account.Acc.UserId userIdObj = new HLE.HOS.Services.Account.Acc.UserId(name);
            _accountManager.OpenUser(userIdObj);
        }

        [UnmanagedCallersOnly(EntryPoint = "close_user")]
        public static void CloseUser(IntPtr userId)
        {
            string name = Marshal.PtrToStringAnsi(userId);

            HLE.HOS.Services.Account.Acc.UserId userIdObj = new HLE.HOS.Services.Account.Acc.UserId(name);
            _accountManager.OpenUser(userIdObj);
        }

        [UnmanagedCallersOnly(EntryPoint = "free_avatars")]
        public static unsafe void FreeAvatars(AvatarArray avatarArray)
        {
            if (avatarArray.Avatars != null)
            {
                for (int i = 0; i < avatarArray.Count; i++)
                {
                    if (avatarArray.Avatars[i].ImageData != null)
                        Marshal.FreeHGlobal((IntPtr)avatarArray.Avatars[i].ImageData);
                    if (avatarArray.Avatars[i].FileName != null)
                        Marshal.FreeHGlobal((IntPtr)avatarArray.Avatars[i].FileName);
                }
                Marshal.FreeHGlobal((IntPtr)avatarArray.Avatars);
            }
        }


        [UnmanagedCallersOnly(EntryPoint = "get_avatars")]
        public static unsafe AvatarArray GetAvatars()
        {
            var avatars = AvatarLoader.LoadAvatars(_contentManager, _virtualFileSystem);
            int count = avatars.Count;

            AvatarInfo* avatarInfos = (AvatarInfo*)Marshal.AllocHGlobal(sizeof(AvatarInfo) * count);

            int index = 0;
            foreach (var kvp in avatars)
            {
                string fileName = kvp.Key;
                byte[] imageData = kvp.Value;

                byte* imagePtr = (byte*)Marshal.AllocHGlobal(imageData.Length);
                Marshal.Copy(imageData, 0, (IntPtr)imagePtr, imageData.Length);

                byte[] utf8FileName = Encoding.UTF8.GetBytes(fileName);
                sbyte* fileNamePtr = (sbyte*)Marshal.AllocHGlobal(utf8FileName.Length + 1);
                for (int i = 0; i < utf8FileName.Length; i++)
                {
                    fileNamePtr[i] = (sbyte)utf8FileName[i];
                }
                fileNamePtr[utf8FileName.Length] = 0; 

                avatarInfos[index] = new AvatarInfo
                {
                    ImageData = imagePtr,
                    ImageSize = imageData.Length,
                    FileName = fileNamePtr
                };

                index++;
            }

            return new AvatarArray
            {
                Count = count,
                Avatars = avatarInfos
            };
        }

        [UnmanagedCallersOnly(EntryPoint = "refresh_account_manager")]
        public static void RefreshAccountManager()
        {
            _accountManager.Refresh();
        }
        
        [UnmanagedCallersOnly(EntryPoint = "get_dlc_nca_list")]
        public static unsafe DlcNcaList GetDlcNcaList(IntPtr titleIdPtr, IntPtr pathPtr) 
        {
            var titleId = Marshal.PtrToStringAnsi(titleIdPtr);
            var containerPath = Marshal.PtrToStringAnsi(pathPtr);

            if (!File.Exists(containerPath))
            {
                return new DlcNcaList { success = false };
            }

            using FileStream containerFile = File.OpenRead(containerPath);

            PartitionFileSystem pfs = new();
            pfs.Initialize(containerFile.AsStorage()).ThrowIfFailure();
            bool containsDlc = false;

            _virtualFileSystem.ImportTickets(pfs);

            List<DlcNcaListItem> listItems = new();
            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
            {
                using var ncaFile = new UniqueRef<IFile>();

                pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryCreateNca(ncaFile.Get.AsStorage(), containerPath);

                if (nca == null)
                {
                    continue;
                }

                if (nca.Header.ContentType == NcaContentType.PublicData)
                {
                    if ((nca.Header.TitleId & 0xFFFFFFFFFFFFE000).ToString("x16") != titleId)
                    {
                        break;
                    }

                    Logger.Warning?.Print(LogClass.Application, $"ContainerPath: {containerPath}");
                    Logger.Warning?.Print(LogClass.Application, $"TitleId: {nca.Header.TitleId}");
                    Logger.Warning?.Print(LogClass.Application, $"fileEntry.FullPath: {fileEntry.FullPath}");
                    
                    DlcNcaListItem item = new();
                    CopyStringToFixedArray(fileEntry.FullPath, item.Path, 256);
                    item.TitleId = nca.Header.TitleId;
                    listItems.Add(item);
                    
                    containsDlc = true;
                }
            }

            if (!containsDlc)
            {
                Console.WriteLine("The specified file does not contain DLC for the selected title!");
                return new DlcNcaList { success = false };
                // GtkDialog.CreateErrorDialog("The specified file does not contain DLC for the selected title!");
            }
            
            var list = new DlcNcaList { success = true, size = (uint) listItems.Count };

            DlcNcaListItem[] items = listItems.ToArray();

            fixed (DlcNcaListItem* p = &items[0])
            {
                list.items = p;
            }
            
            return list;
        }

        private static Nca TryCreateNca(IStorage ncaStorage, string containerPath)
        {
            try
            {
                return new Nca(_virtualFileSystem.KeySet, ncaStorage);
            }
            catch (Exception exception)
            {
                // ignored
            }

            return null;
        }

        [UnmanagedCallersOnly(EntryPoint = "attach_gamepad")]    
        public static IntPtr AttachGamepad(IntPtr namePtr, IntPtr idPtr)
        {
            if (namePtr == IntPtr.Zero || idPtr == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            string value = Marshal.PtrToStringAnsi(namePtr);

            return NativeGamepadDriver.AttachGamepad(value, idPtr);
        }

        [UnmanagedCallersOnly(EntryPoint = "detach_gamepad")]    
        public static void DetachGamepad(IntPtr idPtr)
        {
            NativeGamepadDriver.DetachGamepad(idPtr);
        }

        [UnmanagedCallersOnly(EntryPoint = "set_gamepad_button_state")] 
        public static void SetButtonState(IntPtr idPtr, int buttonId, byte pressed)
        {
            NativeGamepadDriver.SetButtonState(idPtr, buttonId, pressed);
        }

        [UnmanagedCallersOnly(EntryPoint = "set_gamepad_stick_axis")] 
        public static void SetStickAxis(IntPtr idPtr, int stickId, float x, float y)
        {
            NativeGamepadDriver.SetStickAxis(idPtr, stickId, x, y);
        }

        [UnmanagedCallersOnly(EntryPoint = "set_gamepad_motion_axis")] 
        static void SetMotionData(IntPtr idPtr, int motionType, float x, float y, float z)
        {
            NativeGamepadDriver.SetMotionData(idPtr, motionType, x, y, z);
        }

        [UnmanagedCallersOnly(EntryPoint = "set_gamepad_configuration")] 
        unsafe static void setGamepadConfiguration(int argCount, IntPtr* pArgs)
        {
            try
            {
                ControllerOptions Get(int argCount, IntPtr* pArgs)
                {
                    string[] args = new string[argCount];

                    try
                    {
                        for (int i = 0; i < argCount; i++)
                        {
                            args[i] = Marshal.PtrToStringAnsi(pArgs[i]);

                            Console.WriteLine(args[i]);
                        }

                        ControllerOptions options = null;
                        Parser.Default.ParseArguments<ControllerOptions>(args)
                            .WithParsed(option => options = option)
                            .WithNotParsed(errors => errors.Output());
                        return options;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.ToString());
                        return null;
                    }
                }

                ControllerOptions option = Get(argCount, pArgs);
                if (option == null)
                {
                    return;
                }

                static void LoadPlayerConfiguration(
                    List<InputConfig> inputConfiguration,
                    string inputProfileName,
                    string inputId,
                    string inputDSUServer,
                    PlayerIndex index,
                    ControllerOptions option)
                {
                    if (inputId == null)
                    {
                        return;
                    }

                    InputConfig inputConfig = HandlePlayerConfiguration(inputProfileName, inputId, inputDSUServer, index, null, option);

                    if (inputConfig != null)
                    {
                        inputConfiguration.Add(inputConfig);
                    }
                }

                List<InputConfig> inputConfiguration = new();

                LoadPlayerConfiguration(inputConfiguration, null, option.InputId1, null, PlayerIndex.Player1, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId2, null, PlayerIndex.Player2, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId3, null, PlayerIndex.Player3, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId4, null, PlayerIndex.Player4, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId5, null, PlayerIndex.Player5, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId6, null, PlayerIndex.Player6, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId7, null, PlayerIndex.Player7, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputId8, null, PlayerIndex.Player8, option);
                LoadPlayerConfiguration(inputConfiguration, null, option.InputIdHandheld, null, PlayerIndex.Handheld, option);

                _inputConfiguration = inputConfiguration;
                Logger.Info?.Print(LogClass.Application, $"Configured {inputConfiguration.Count} gamepads, {inputConfiguration} from native code.");

                Volatile.Read(ref _currentSession)?.TryUseWindow(
                    window => window.NpadManager?.ReloadConfiguration(inputConfiguration, _enableKeyboard, _enableMouse));
            }
            catch (Exception exception)
            {
                // Exceptions must not cross the unmanaged ABI boundary.
                Logger.Error?.Print(LogClass.Application, $"Failed to update gamepad configuration: {exception}");
            }
        }


        [UnmanagedCallersOnly(EntryPoint = "get_current_fps")]
        public static unsafe int GetFPS() 
        {
            try
            {
                EmulationSession session = Volatile.Read(ref _currentSession);

                return session != null &&
                    session.TryUseWindow(
                        window => window.Device == null ? 0 : (int)window.Device.Statistics.GetGameFrameRate(),
                        out int framesPerSecond)
                    ? framesPerSecond
                    : 0;
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to get current FPS: {exception}");
                return 0;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "set_game_volume")]
        public static unsafe void SetGameVolume(float volume)
        {
            try
            {
                Volatile.Read(ref _currentSession)?.TryUseEmulationContext(
                    emulationContext => emulationContext.SetVolume(volume));
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to set game volume: {exception}");
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "get_game_volume")]
        public static unsafe float GetGameVolume()
        {
            try
            {
                EmulationSession session = Volatile.Read(ref _currentSession);

                return session != null &&
                    session.TryUseEmulationContext(
                        emulationContext => (float)emulationContext.GetVolume(),
                        out float volume)
                    ? volume
                    : 0;
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to get game volume: {exception}");
                return 0;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "initialize")]
        public static unsafe void Initialize()
        {
            try
            {
                InitializeCore();
                Interlocked.Exchange(ref _processInitializationFailure, null);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(
                    ref _processInitializationFailure,
                    ExceptionDispatchInfo.Capture(exception));
                LogAbiBoundaryFailure("initialize", exception);
            }
        }

        private static void InitializeCore()
        {
            AppDataManager.Initialize(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            if (_virtualFileSystem == null)
            {
                _virtualFileSystem = VirtualFileSystem.CreateInstance();
            }

            EnsureLibHacHorizonManagerInitialized();

            if (_contentManager == null)
            {
                _contentManager = new ContentManager(_virtualFileSystem);
            }
            
            if (_accountManager == null)
            {
                _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient);
            }

            lock (inputManagerLock)
            {
                if (_inputManager == null)
                {
                    SDL2KeyboardDriver keyboardDriver = null;
                    NativeGamepadDriver gamepadDriver = null;

                    try
                    {
                        keyboardDriver = new SDL2KeyboardDriver();
                        gamepadDriver = new NativeGamepadDriver();
                        _inputManager = new InputManager(keyboardDriver, gamepadDriver);
                    }
                    catch
                    {
                        gamepadDriver?.Dispose();
                        keyboardDriver?.Dispose();
                        throw;
                    }
                }
            }

            GCSettings.LatencyMode = GCLatencyMode.Batch;
        }

        private static void EnsureLibHacHorizonManagerInitialized()
        {
            lock (horizonManagerLock)
            {
                if (_libHacHorizonManager != null)
                {
                    return;
                }

                LibHacHorizonManager manager = new();
                manager.InitializeFsServer(_virtualFileSystem);
                manager.InitializeArpServer();
                manager.InitializeBcatServer();
                manager.InitializeSystemClients();
                _libHacHorizonManager = manager;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "initialize-dualmapped")]
        public static unsafe bool InitializeDM()
        {
            try
            {
                return Cpu.LightningJit.DualMappedTranslator.InitializeDualMapped();
            }
            catch (Exception exception)
            {
                LogAbiBoundaryFailure("initialize-dualmapped", exception);
                return false;
            }
        }

        private static void LogAbiBoundaryFailure(string entrypoint, Exception exception)
        {
            try
            {
                Logger.Error?.Print(
                    LogClass.Application,
                    $"Native entrypoint {entrypoint} failed: {exception}");
            }
            catch
            {
                try
                {
                    Console.Error.WriteLine(
                        $"Native entrypoint {entrypoint} failed: {exception}");
                }
                catch
                {
                }
            }
        }

        static void Main(string[] args)
        {
            // Make process DPI aware for proper window sizing on high-res screens.
            ForceDpiAware.Windows();

            Silk.NET.Core.Loader.SearchPathContainer.Platform = Silk.NET.Core.Loader.UnderlyingPlatform.MacOS;

            if (!OperatingSystem.IsIOS())
            {
                // Console.Title = $"Ryujinx Console {Version} (Headless SDL2)";
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsLinux())
            {
                if (Interlocked.CompareExchange(ref _mainThreadDispatcherRegistered, 1, 0) == 0)
                {
                    // MacOS must perform SDL polls from the main thread. Keep one
                    // process-owned dispatcher and one completion barrier per call.
                    SDL2Driver.MainThreadDispatcher = DispatchSdlActionToMainThread;
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                MVKInitialization.InitializeResolver();
            }

            Parser.Default.ParseArguments<Options>(args)
            .WithParsed(Load)
            .WithNotParsed(errors => errors.Output());
        }

        private static void DispatchSdlActionToMainThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            using ManualResetEventSlim completed = new(false);
            ExceptionDispatchInfo failure = null;

            if (!WindowBase.TryQueueMainThreadAction(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    completed.Set();
                }
            }))
            {
                throw new OperationCanceledException(
                    "The emulation main-thread dispatcher is not accepting work.");
            }

            completed.Wait();
            failure?.Throw();
        }
    
        [UnmanagedCallersOnly(EntryPoint = "install_firmware")]
        public static IntPtr InstallFirmwareNative(IntPtr inputPtr)
        {
            // ✖ is to tell if its an error or not because i'm too lazy to make a bool 
            try
            {
                if (inputPtr == IntPtr.Zero)
                {
                    return Marshal.StringToHGlobalAnsi("Error: inputPtr is null. ✖");
                }

                string inputString = Marshal.PtrToStringAnsi(inputPtr);

                if (string.IsNullOrEmpty(inputString))
                {
                    return Marshal.StringToHGlobalAnsi("Error: inputString is null or empty. ✖");
                }
                
                string firmwareVersion = InstallFirmware(inputString);

                return Marshal.StringToHGlobalAnsi(firmwareVersion);
            }
            catch (Exception ex)
            {
                return Marshal.StringToHGlobalAnsi($"Error: {ex.Message} ✖");
            }
        }

        public static string InstallFirmware(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            }

            if (_contentManager == null)
            {
                throw new InvalidOperationException("_contentManager is not initialized.");
            }

            SystemVersion systemVersion = _contentManager.VerifyFirmwarePackage(filePath);
            if (systemVersion is null)
            {
                throw new InvalidOperationException("The provided file is not a valid firmware package.");
            }

            Task.Run(() => _contentManager.InstallFirmware(filePath));

            return systemVersion.VersionString;
        }


        [UnmanagedCallersOnly(EntryPoint = "installed_firmware_version")]
        public static IntPtr GetInstalledFirmwareVersionNative()
        {
            var result = GetInstalledFirmwareVersion();
            return Marshal.StringToHGlobalAnsi(result);
        }

        [UnmanagedCallersOnly(EntryPoint = "free_firmware_version")]
        public static void FreeFirmwareVersion(IntPtr versionPtr)
        {
            if (versionPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(versionPtr);
            }
        }

        public static string GetInstalledFirmwareVersion()
        {
            try
            {
                var version = _contentManager.GetCurrentFirmwareVersion();

                if (version != null)
                {
                    return version.VersionString;
                }

                return String.Empty;
            } catch
            {
                return String.Empty;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "pause_emulation")]
        public static void PauseEmulation(bool shouldPause)
        {
            try
            {
                Volatile.Read(ref _currentSession)?.TryUseEmulationContext(
                    emulationContext => emulationContext.System?.TogglePauseEmulation(shouldPause));
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to update emulation pause state: {exception}");
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "prepare_emulation_start")]
        public static void PrepareEmulationStart()
        {
            if (Volatile.Read(ref _currentSession) != null)
            {
                return;
            }

            Interlocked.Exchange(ref _pendingStopRequest, 0);
            Volatile.Write(ref _startPending, 1);
        }

        [UnmanagedCallersOnly(EntryPoint = "cancel_emulation_start")]
        public static void CancelEmulationStart()
        {
            lock (metalLayerLock)
            {
                Volatile.Write(ref _startPending, 0);
                Interlocked.Exchange(ref _pendingStopRequest, 0);

                // Runner rejection means this layer never became session-owned.
                // A late cancel must not detach a live session's retained layer.
                if (Volatile.Read(ref _currentSession) == null)
                {
                    _pendingNativeMetalLayer = IntPtr.Zero;
                }
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "stop_emulation")]
        public static void StopEmulation()
        {
            try
            {
                EmulationSession session = Volatile.Read(ref _currentSession);

                if (session != null)
                {
                    session.RequestStop();
                    return;
                }

                if (Volatile.Read(ref _startPending) == 0)
                {
                    return;
                }

                Interlocked.Exchange(ref _pendingStopRequest, 1);
                session = Volatile.Read(ref _currentSession);

                if (session != null)
                {
                    Interlocked.Exchange(ref _pendingStopRequest, 0);
                    session.RequestStop();
                }
                else if (Volatile.Read(ref _startPending) == 0)
                {
                    // The pending start either consumed the request or completed.
                    // Do not let a late Stop request affect the next session.
                    Interlocked.Exchange(ref _pendingStopRequest, 0);
                }
            }
            catch (Exception exception)
            {
                // Exceptions must not cross the unmanaged ABI boundary.
                Logger.Error?.Print(LogClass.Application, $"Failed to request emulation stop: {exception}");
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "get_game_info")]
        public static GameInfoNative GetGameInfoNative(int descriptor, IntPtr extensionPtr)
        {
            if (_virtualFileSystem == null) {
                _virtualFileSystem = VirtualFileSystem.CreateInstance();
            }
            
            var extension = Marshal.PtrToStringAnsi(extensionPtr);
            var stream = OpenFile(descriptor);

            var gameInfo = GetGameInfo(stream, extension);

            if (gameInfo == null) {
                return new GameInfoNative(0, "", "", "", "", new byte[0]);
            }

            return new GameInfoNative(
                (ulong)gameInfo.FileSize, 
                gameInfo.TitleName + "\0", 
                gameInfo.TitleId + "\0", 
                gameInfo.Developer + "\0", 
                gameInfo.Version + "\0", 
                gameInfo.Icon
            );
        }

        public static GameInfo? GetGameInfo(Stream gameStream, string extension)
        {

            var gameInfo = new GameInfo
            {
                FileSize = gameStream.Length * 0.000000000931,
                TitleName = "Unknown",
                TitleId = "0000000000000000",
                Developer = "Unknown",
                Version = "0",
                Icon = null
            };

            const Language TitleLanguage = Language.AmericanEnglish;

            BlitStruct<ApplicationControlProperty> controlHolder = new(1);

            try
            {
                try
                {
                    if (extension == "nsp" || extension == "pfs0" || extension == "xci")
                    {
                        IFileSystem pfs;

                        bool isExeFs = false;

                        if (extension == "xci")
                        {
                            Xci xci = new(_virtualFileSystem.KeySet, gameStream.AsStorage());

                            pfs = xci.OpenPartition(XciPartitionType.Secure);
                        }
                        else
                        {
                            var pfsTemp = new PartitionFileSystem();
                            pfsTemp.Initialize(gameStream.AsStorage()).ThrowIfFailure();
                            pfs = pfsTemp;

                            // If the NSP doesn't have a main NCA, decrement the number of applications found and then continue to the next application.
                            bool hasMainNca = false;

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
                            {
                                if (Path.GetExtension(fileEntry.FullPath).ToLower() == ".nca")
                                {
                                    using UniqueRef<IFile> ncaFile = new();

                                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    Nca nca = new(_virtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                                    int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                    // Some main NCAs don't have a data partition, so check if the partition exists before opening it
                                    if (nca.Header.ContentType == NcaContentType.Program && !(nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection()))
                                    {
                                        hasMainNca = true;

                                        break;
                                    }
                                }
                                else if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                                {
                                    isExeFs = true;
                                }
                            }

                            if (!hasMainNca && !isExeFs)
                            {
                                return null;
                            }
                        }

                        if (isExeFs)
                        {
                            using UniqueRef<IFile> npdmFile = new();

                            LibHac.Result result = pfs.OpenFile(ref npdmFile.Ref, "/main.npdm".ToU8Span(), OpenMode.Read);

                            if (ResultFs.PathNotFound.Includes(result))
                            {
                                Npdm npdm = new(npdmFile.Get.AsStream());

                                gameInfo.TitleName = npdm.TitleName;
                                gameInfo.TitleId = npdm.Aci0.TitleId.ToString("x16");
                            }
                        }
                        else
                        {
                            GetControlFsAndTitleId(pfs, out IFileSystem? controlFs, out string? id);

                            gameInfo.TitleId = id;

                            if (controlFs == null)
                            {
                                Logger.Error?.Print(LogClass.Application, $"No control FS was returned. Unable to process game any further: {gameInfo.TitleName}");
                                return null;
                            }

                            // Check if there is an update available.
                            if (IsUpdateApplied(gameInfo.TitleId, out IFileSystem? updatedControlFs))
                            {
                                // Replace the original ControlFs by the updated one.
                                controlFs = updatedControlFs;
                            }

                            ReadControlData(controlFs, controlHolder.ByteSpan);


                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out gameInfo.TitleId, out gameInfo.Developer, out gameInfo.Version);

                            // Read the icon from the ControlFS and store it as a byte array
                            try
                            {
                                using UniqueRef<IFile> icon = new();

                                controlFs?.OpenFile(ref icon.Ref, $"/icon_{TitleLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                using MemoryStream stream = new();

                                icon.Get.AsStream().CopyTo(stream);
                                gameInfo.Icon = stream.ToArray();
                            }
                            catch (HorizonResultException)
                            {
                                foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                                {
                                    if (entry.Name == "control.nacp")
                                    {
                                        continue;
                                    }

                                    using var icon = new UniqueRef<IFile>();

                                    controlFs?.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    using MemoryStream stream = new();

                                    icon.Get.AsStream().CopyTo(stream);
                                    gameInfo.Icon = stream.ToArray();

                                    if (gameInfo.Icon != null)
                                    {
                                        break;
                                    }
                                }

                            }
                        }
                    }
                    else if (extension == "nro")
                    {
                        BinaryReader reader = new(gameStream);

                        byte[] Read(long position, int size)
                        {
                            gameStream.Seek(position, SeekOrigin.Begin);

                            return reader.ReadBytes(size);
                        }

                        gameStream.Seek(24, SeekOrigin.Begin);

                        int assetOffset = reader.ReadInt32();

                        if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                        {
                            byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                            long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                            long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                            ulong nacpOffset = reader.ReadUInt64();
                            ulong nacpSize = reader.ReadUInt64();

                            // Reads and stores game icon as byte array
                            if (iconSize > 0)
                            {
                                gameInfo.Icon = Read(assetOffset + iconOffset, (int)iconSize);
                            }

                            // Read the NACP data
                            Read(assetOffset + (int)nacpOffset, (int)nacpSize).AsSpan().CopyTo(controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out gameInfo.TitleId, out gameInfo.Developer, out gameInfo.Version);
                        }
                    }
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");
                }
                catch (InvalidDataException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. {exception}");
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The gameStream encountered was not of a valid type. Error: {exception}");

                    return null;
                }
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);
            }

            void ReadControlData(IFileSystem? controlFs, Span<byte> outProperty)
            {
                using UniqueRef<IFile> controlFile = new();

                controlFs?.OpenFile(ref controlFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                controlFile.Get.Read(out _, 0, outProperty, ReadOption.None).ThrowIfFailure();
            }

            void GetGameInformation(ref ApplicationControlProperty controlData, out string? titleName, out string titleId, out string? publisher, out string? version)
            {
                _ = Enum.TryParse(TitleLanguage.ToString(), out TitleLanguage desiredTitleLanguage);

                if (controlData.Title.ItemsRo.Length > (int)desiredTitleLanguage)
                {
                    titleName = controlData.Title[(int)desiredTitleLanguage].NameString.ToString();
                    publisher = controlData.Title[(int)desiredTitleLanguage].PublisherString.ToString();
                }
                else
                {
                    titleName = null;
                    publisher = null;
                }

                if (string.IsNullOrWhiteSpace(titleName))
                {
                    foreach (ref readonly var controlTitle in controlData.Title.ItemsRo)
                    {
                        if (!controlTitle.NameString.IsEmpty())
                        {
                            titleName = controlTitle.NameString.ToString();

                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(publisher))
                {
                    foreach (ref readonly var controlTitle in controlData.Title.ItemsRo)
                    {
                        if (!controlTitle.PublisherString.IsEmpty())
                        {
                            publisher = controlTitle.PublisherString.ToString();

                            break;
                        }
                    }
                }

                if (controlData.PresenceGroupId != 0)
                {
                    titleId = controlData.PresenceGroupId.ToString("x16");
                }
                else if (controlData.SaveDataOwnerId != 0)
                {
                    titleId = controlData.SaveDataOwnerId.ToString();
                }
                else if (controlData.AddOnContentBaseId != 0)
                {
                    titleId = (controlData.AddOnContentBaseId - 0x1000).ToString("x16");
                }
                else
                {
                    titleId = "0000000000000000";
                }

                version = controlData.DisplayVersionString.ToString();
            }

            void GetControlFsAndTitleId(IFileSystem pfs, out IFileSystem? controlFs, out string? titleId)
            {
                (_, _, Nca? controlNca) = GetGameData(_virtualFileSystem, pfs, 0);

                if (controlNca == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "Control NCA is null. Unable to load control FS.");
                }

                // Return the ControlFS
                controlFs = controlNca?.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                titleId = controlNca?.Header.TitleId.ToString("x16");
            }

            (Nca? mainNca, Nca? patchNca, Nca? controlNca) GetGameData(VirtualFileSystem fileSystem, IFileSystem pfs, int programIndex)
            {
                Nca? mainNca = null;
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    Logger.Info?.Print(LogClass.Application, $"Loading file from PFS: {fileEntry.FullPath}");

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                        if (nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                        {
                            patchNca = nca;
                        }
                        else
                        {
                            mainNca = nca;
                        }
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (mainNca, patchNca, controlNca);
            }

            bool IsUpdateApplied(string? titleId, out IFileSystem? updatedControlFs)
            {
                updatedControlFs = null;

                string? updatePath = "(unknown)";

                if (_virtualFileSystem == null)
                {
                    Logger.Error?.Print(LogClass.Application, "SwitchDevice was not initialized.");
                    return false;
                }

                try
                {
                    (Nca? patchNca, Nca? controlNca) = GetGameUpdateData(_virtualFileSystem, titleId, 0, out updatePath);

                    if (patchNca != null && controlNca != null)
                    {
                        updatedControlFs = controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);

                        return true;
                    }
                }
                catch (InvalidDataException)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {updatePath}");
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}. Errored File: {updatePath}");
                }

                return false;
            }

            (Nca? patch, Nca? control) GetGameUpdateData(VirtualFileSystem fileSystem, string? titleId, int programIndex, out string? updatePath)
            {
                updatePath = "";

                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdBase))
                {
                    // Clear the program index part.
                    titleIdBase &= ~0xFUL;

                    // Load update information if exists.
                    string titleUpdateMetadataPath = Path.Combine(AppDataManager.GamesDirPath, titleIdBase.ToString("x16"), "updates.json");

                    if (File.Exists(titleUpdateMetadataPath))
                    {
                        string updatePathRelative = JsonHelper.DeserializeFromFile(titleUpdateMetadataPath, _titleSerializerContext.TitleUpdateMetadata).Selected;
                        updatePath = Path.Combine(AppDataManager.BaseDirPath, updatePathRelative);

                        if (File.Exists(updatePath))
                        {
                            FileStream file = new(updatePath, FileMode.Open, FileAccess.Read);
                            PartitionFileSystem nsp = new();
                            nsp.Initialize(file.AsStorage()).ThrowIfFailure();

                            return GetGameUpdateDataFromPartition(fileSystem, nsp, titleIdBase.ToString("x16"), programIndex);
                        }
                    }
                }

                return (null, null);
            }

            (Nca? patchNca, Nca? controlNca) GetGameUpdateDataFromPartition(VirtualFileSystem fileSystem, PartitionFileSystem pfs, string titleId, int programIndex)
            {
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if ($"{nca.Header.TitleId.ToString("x16")[..^3]}000" != titleId)
                    {
                        break;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        patchNca = nca;
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (patchNca, controlNca);
            }

            return gameInfo;
        }

        static ControllerType GetControllerTypeByIndex(Options optionsInstance, int index)
        {
            if (index < 0 || index > 7)
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be between 0 and 7.");

            string propertyName = $"controllerType{index + 1}";
            var property = typeof(Options).GetProperty(propertyName);

            if (property == null)
                throw new InvalidOperationException($"Property '{propertyName}' not found in Options.");

            return (ControllerType)property.GetValue(optionsInstance);
        }

        static ControllerType GetControllerTypeByIndex(ControllerOptions optionsInstance, int index)
        {
            if (index < 0 || index > 7)
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be between 0 and 7.");

            string propertyName = $"controllerType{index + 1}";
            var property = typeof(ControllerOptions).GetProperty(propertyName);

            if (property == null)
                throw new InvalidOperationException($"Property '{propertyName}' not found in ControllerOptions.");

            return (ControllerType)property.GetValue(optionsInstance);
        }
        
#nullable enable
        private static InputConfig HandlePlayerConfiguration(string inputProfileName, string inputId, string inputDSUServer, PlayerIndex index, Options? option, ControllerOptions? controllerOptions = null)
#nullable disable
        {
            if (inputId == null)
            {
                Logger.Info?.Print(LogClass.Application, $"{index} not configured");

                return null;
            }

            IGamepad gamepad;

            bool isKeyboard = true;

            gamepad = _inputManager.KeyboardDriver.GetGamepad(inputId);

            if (gamepad == null)
            {
                gamepad = _inputManager.GamepadDriver.GetGamepad(inputId);
                isKeyboard = false;

                if (gamepad == null)
                {
                    Logger.Error?.Print(LogClass.Application, $"{index} gamepad not found (\"{inputId}\")");

                    inputId = "0";

                    gamepad = _inputManager.KeyboardDriver.GetGamepad(inputId);

                    isKeyboard = true;
                }
            }


            gamepad.Dispose();

            InputConfig config;

            if (inputProfileName == null || inputProfileName.Equals("default"))
            {
                if (isKeyboard)
                {
                    config = new StandardKeyboardInputConfig
                    {
                        Version = InputConfig.CurrentVersion,
                        Backend = InputBackendType.WindowKeyboard,
                        Id = null,
                        ControllerType = ControllerType.JoyconPair,
                        LeftJoycon = new LeftJoyconCommonConfig<Key>
                        {
                            DpadUp = Key.Up,
                            DpadDown = Key.Down,
                            DpadLeft = Key.Left,
                            DpadRight = Key.Right,
                            ButtonMinus = Key.Minus,
                            ButtonL = Key.E,
                            ButtonZl = Key.Q,
                            ButtonSl = Key.Unbound,
                            ButtonSr = Key.Unbound,
                        },

                        LeftJoyconStick = new JoyconConfigKeyboardStick<Key>
                        {
                            StickUp = Key.W,
                            StickDown = Key.S,
                            StickLeft = Key.A,
                            StickRight = Key.D,
                            StickButton = Key.F,
                        },

                        RightJoycon = new RightJoyconCommonConfig<Key>
                        {
                            ButtonA = Key.Z,
                            ButtonB = Key.X,
                            ButtonX = Key.C,
                            ButtonY = Key.V,
                            ButtonPlus = Key.Plus,
                            ButtonR = Key.U,
                            ButtonZr = Key.O,
                            ButtonSl = Key.Unbound,
                            ButtonSr = Key.Unbound,
                        },

                        RightJoyconStick = new JoyconConfigKeyboardStick<Key>
                        {
                            StickUp = Key.I,
                            StickDown = Key.K,
                            StickLeft = Key.J,
                            StickRight = Key.L,
                            StickButton = Key.H,
                        },
                    };
                }
                else
                {
                    bool isNintendoStyle = true; // gamepadName.Contains("Nintendo") || gamepadName.Contains("Joycons");

                    ControllerType currentController;

                    if (index == PlayerIndex.Handheld)
                    {
                        currentController = ControllerType.Handheld;
                    }
                    else
                    {
                        if (option == null)
                        {
                            if (controllerOptions != null)
                            {
                                currentController = GetControllerTypeByIndex(controllerOptions, (int)index);
                            }
                            else
                            {
                                currentController = ControllerType.JoyconPair;
                            }
                        } else
                        {
                            currentController = GetControllerTypeByIndex(option, (int)index);
                        }
                    }

                    Console.WriteLine($"Configuring {inputId} as {currentController} ({index})");

                    config = new StandardControllerInputConfig
                    {
                        Version = InputConfig.CurrentVersion,
                        Backend = InputBackendType.GamepadSDL2,
                        Id = null,
                        ControllerType = currentController,
                        DeadzoneLeft = 0.1f,
                        DeadzoneRight = 0.1f,
                        RangeLeft = 1.0f,
                        RangeRight = 1.0f,
                        TriggerThreshold = 0.5f,
                        LeftJoycon = new LeftJoyconCommonConfig<ConfigGamepadInputId>
                        {
                            DpadUp = ConfigGamepadInputId.DpadUp,
                            DpadDown = ConfigGamepadInputId.DpadDown,
                            DpadLeft = ConfigGamepadInputId.DpadLeft,
                            DpadRight = ConfigGamepadInputId.DpadRight,
                            ButtonMinus = ConfigGamepadInputId.Minus,
                            ButtonL = ConfigGamepadInputId.LeftShoulder,
                            ButtonZl = ConfigGamepadInputId.LeftTrigger,
                            ButtonSl = ConfigGamepadInputId.Unbound,
                            ButtonSr = ConfigGamepadInputId.Unbound,
                        },

                        LeftJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                        {
                            Joystick = ConfigStickInputId.Left,
                            StickButton = ConfigGamepadInputId.LeftStick,
                            InvertStickX = false,
                            InvertStickY = false,
                            Rotate90CW = false,
                        },

                        RightJoycon = new RightJoyconCommonConfig<ConfigGamepadInputId>
                        {
                            ButtonA = isNintendoStyle ? ConfigGamepadInputId.A : ConfigGamepadInputId.B,
                            ButtonB = isNintendoStyle ? ConfigGamepadInputId.B : ConfigGamepadInputId.A,
                            ButtonX = isNintendoStyle ? ConfigGamepadInputId.X : ConfigGamepadInputId.Y,
                            ButtonY = isNintendoStyle ? ConfigGamepadInputId.Y : ConfigGamepadInputId.X,
                            ButtonPlus = ConfigGamepadInputId.Plus,
                            ButtonR = ConfigGamepadInputId.RightShoulder,
                            ButtonZr = ConfigGamepadInputId.RightTrigger,
                            ButtonSl = ConfigGamepadInputId.Unbound,
                            ButtonSr = ConfigGamepadInputId.Unbound,
                        },

                        RightJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                        {
                            Joystick = ConfigStickInputId.Right,
                            StickButton = ConfigGamepadInputId.RightStick,
                            InvertStickX = false,
                            InvertStickY = false,
                            Rotate90CW = false,
                        },

                        Motion = new StandardMotionConfigController
                        {
                            MotionBackend = MotionInputBackendType.GamepadDriver,
                            EnableMotion = true,
                            Sensitivity = 100,
                            GyroDeadzone = 1,
                        },
                        Rumble = new RumbleConfigController
                        {
                            StrongRumble = 1f,
                            WeakRumble = 1f,
                            EnableRumble = true,
                        },
                    };
                }
            }
            else
            {
                string profileBasePath;

                if (isKeyboard)
                {
                    profileBasePath = Path.Combine(AppDataManager.ProfilesDirPath, "keyboard");
                }
                else
                {
                    profileBasePath = Path.Combine(AppDataManager.ProfilesDirPath, "controller");
                }

                string path = Path.Combine(profileBasePath, inputProfileName + ".json");

                if (!File.Exists(path))
                {
                    Logger.Error?.Print(LogClass.Application, $"Input profile \"{inputProfileName}\" not found for \"{inputId}\"");

                    return null;
                }

                try
                {
                    config = JsonHelper.DeserializeFromFile(path, _serializerContext.InputConfig);
                }
                catch (JsonException)
                {
                    Logger.Error?.Print(LogClass.Application, $"Input profile \"{inputProfileName}\" parsing failed for \"{inputId}\"");

                    return null;
                }
            }

            config.Id = inputId;
            config.PlayerIndex = index;

            string inputTypeName = isKeyboard ? "Keyboard" : "Gamepad";

            Logger.Info?.Print(LogClass.Application, $"{config.PlayerIndex} configured with {inputTypeName} \"{config.Id}\"");

            // If both stick ranges are 0 (usually indicative of an outdated profile load) then both sticks will be set to 1.0.
            if (config is StandardControllerInputConfig controllerConfig)
            {
                if (controllerConfig.RangeLeft <= 0.0f && controllerConfig.RangeRight <= 0.0f)
                {
                    controllerConfig.RangeLeft = 1.0f;
                    controllerConfig.RangeRight = 1.0f;

                    Logger.Info?.Print(LogClass.Application, $"{config.PlayerIndex} stick range reset. Save the profile now to update your configuration");
                }
            }

            return config;
        }

        static void Load(Options option)
        {
            EmulationSession session = Volatile.Read(ref _currentSession)
                ?? throw new InvalidOperationException("No active emulation session.");

            if (session.IsStopRequested)
            {
                return;
            }

            EnsureLibHacHorizonManagerInitialized();

            // _contentManager = new ContentManager(_virtualFileSystem);

            _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient, option.UserProfile);

            _userChannelPersistence = new UserChannelPersistence();

            GraphicsConfig.EnableShaderCache = true;

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                if (option.GraphicsBackend == GraphicsBackend.OpenGl)
                {
                    option.GraphicsBackend = GraphicsBackend.Vulkan;
                    Logger.Warning?.Print(LogClass.Application, "OpenGL is not supported on Apple platforms, switching to Vulkan!");
                }
            }

            IGamepad gamepad;

            if (option.ListInputIds)
            {
                Logger.Info?.Print(LogClass.Application, "Input Ids:");

                foreach (string id in _inputManager.KeyboardDriver.GamepadsIds)
                {
                    gamepad = _inputManager.KeyboardDriver.GetGamepad(id);

                    Logger.Info?.Print(LogClass.Application, $"- {id} (\"{gamepad.Name}\")");

                    gamepad.Dispose();
                }

               string[] gamepadsIdsArray = _inputManager.GamepadDriver.GamepadsIds.ToArray();

                foreach (string id in gamepadsIdsArray)
                {
                    gamepad = _inputManager.GamepadDriver.GetGamepad(id);

                    string gamepadsIdsString = $"- {id} (\"{gamepad.Name}\")";

                    Logger.Info?.Print(LogClass.Application, gamepadsIdsString);

                    gamepad.Dispose();
                }

                return;
            }

            if (option.InputPath == null)
            {
                Logger.Error?.Print(LogClass.Application, "Please provide a file to load");

                return;
            }
            

            Match match = Regex.Match(option.InputPath, @"0x[0-9A-Fa-f]+");
            if (match.Success)
            {
                string hexStr = match.Value.Substring(2);
                ulong id = Convert.ToUInt64(hexStr, 16);
                string contentPath = _contentManager.GetInstalledContentPath(id, StorageId.BuiltInSystem, NcaContentType.Program);

                option.InputPath = contentPath;
            }

            _inputConfiguration = new List<InputConfig>();
            _enableKeyboard = option.EnableKeyboard;
            _enableMouse = option.EnableMouse;

            static void LoadPlayerConfiguration(string inputProfileName, string inputId, string inputDSUServer, PlayerIndex index, Options option)
            {
                if (inputId == null)
                {
                    return;
                }

                InputConfig inputConfig = HandlePlayerConfiguration(inputProfileName, inputId, inputDSUServer, index, option);

                if (inputConfig != null)
                {
                    _inputConfiguration.Add(inputConfig);
                }
            }

            LoadPlayerConfiguration(option.InputProfile1Name, option.InputId1, option.InputDSUServer1, PlayerIndex.Player1, option);
            LoadPlayerConfiguration(option.InputProfile2Name, option.InputId2, option.InputDSUServer2, PlayerIndex.Player2, option);
            LoadPlayerConfiguration(option.InputProfile3Name, option.InputId3, option.InputDSUServer3, PlayerIndex.Player3, option);
            LoadPlayerConfiguration(option.InputProfile4Name, option.InputId4, option.InputDSUServer4, PlayerIndex.Player4, option);
            LoadPlayerConfiguration(option.InputProfile5Name, option.InputId5, option.InputDSUServer5, PlayerIndex.Player5, option);
            LoadPlayerConfiguration(option.InputProfile6Name, option.InputId6, option.InputDSUServer6, PlayerIndex.Player6, option);
            LoadPlayerConfiguration(option.InputProfile7Name, option.InputId7, option.InputDSUServer7, PlayerIndex.Player7, option);
            LoadPlayerConfiguration(option.InputProfile8Name, option.InputId8, option.InputDSUServer8, PlayerIndex.Player8, option);
            LoadPlayerConfiguration(option.InputProfileHandheldName, option.InputIdHandheld, option.InputDSUServerHandheld, PlayerIndex.Handheld, option);

            if (_inputConfiguration.Count == 0)
            {
                return;
            }

            // Setup logging level
            Logger.SetEnable(LogLevel.Debug, option.LoggingEnableDebug);
            Logger.SetEnable(LogLevel.Stub, !option.LoggingDisableStub);
            Logger.SetEnable(LogLevel.Info, !option.LoggingDisableInfo);
            Logger.SetEnable(LogLevel.Warning, !option.LoggingDisableWarning);
            Logger.SetEnable(LogLevel.Error, option.LoggingEnableError);
            Logger.SetEnable(LogLevel.Trace, option.LoggingEnableTrace);
            Logger.SetEnable(LogLevel.Guest, !option.LoggingDisableGuest);
            Logger.SetEnable(LogLevel.AccessLog, option.LoggingEnableFsAccessLog);

            if (!option.DisableFileLog)
            {
                string logDir = AppDataManager.LogsDirPath;
                FileStream logFile = null;

                if (!string.IsNullOrEmpty(logDir))
                {
                    logFile = FileLogTarget.PrepareLogFile(logDir);
                }

                if (logFile != null)
                {
                    //Logger.AddTarget(new AsyncLogTargetWrapper(
                    //    new FileLogTarget("file", logFile),
                    //    1000,
                    //    AsyncLogTargetOverflowAction.Block
                    // ));
                }
                else
                {
                    Logger.Error?.Print(LogClass.Application, "No writable log directory available. Make sure either the Logs directory, Application Data, or the Ryujinx directory is writable.");
                }
            }

            if (OperatingSystem.IsIOS()) 
            {
                Logger.Info?.Print(LogClass.Application, $"Current Device: {option.DisplayName} ({option.DeviceModel}) {Environment.OSVersion.Version}");
                Logger.Info?.Print(LogClass.Application, $"Increased Memory Limit: {option.MemoryEnt}");
            }

            if (Interlocked.CompareExchange(ref _unhandledExceptionHandlerRegistered, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.UnhandledException += LogUnhandledException;
            }


            // Setup graphics configuration
            GraphicsConfig.EnableShaderCache = !option.DisableShaderCache;
            GraphicsConfig.EnableTextureRecompression = option.EnableTextureRecompression;
            GraphicsConfig.ResScale = option.ResScale;
            GraphicsConfig.MaxAnisotropy = option.MaxAnisotropy;
            GraphicsConfig.ShadersDumpPath = option.GraphicsShadersDumpPath;
            GraphicsConfig.EnableMacroHLE = !option.DisableMacroHLE;
            GraphicsConfig.EnableColorSpacePassthrough = true;

            DriverUtilities.InitDriverConfig(option.BackendThreading == BackendThreading.Off);
            _virtualFileSystem.ReloadKeySet();
            while (!session.IsStopRequested)
            {
                LoadApplication(option, session);

                if (session.IsStopRequested ||
                    _userChannelPersistence.PreviousIndex == -1 ||
                    !_userChannelPersistence.ShouldRestart)
                {
                    break;
                }

                _userChannelPersistence.ShouldRestart = false;

                if (!session.TryBeginRestart())
                {
                    break;
                }
            }
        }

        private static void LogUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = eventArgs.ExceptionObject as Exception;
            var trace = new System.Diagnostics.StackTrace(exception, true);
            var frame = trace.GetFrame(0);
            string file = frame?.GetFileName();
            int? line = frame?.GetFileLineNumber();

            Logger.Info?.Print(
                LogClass.Application,
                $"Unhandled exception: {exception}\nFile: {file}\nLine: {line}");
        }

        private static void SetupProgressHandler(Switch emulationContext)
        {
            if (emulationContext.Processes.ActiveApplication.DiskCacheLoadState != null)
            {
                emulationContext.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= ProgressHandler;
                emulationContext.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += ProgressHandler;
            }

            emulationContext.Gpu.ShaderCacheStateChanged -= ProgressHandler;
            emulationContext.Gpu.ShaderCacheStateChanged += ProgressHandler;
        }

        private static void ProgressHandler<T>(T state, int current, int total) where T : Enum
        {
            // string label = state switch
            // {
            //    LoadState => $"PTC : {current}/{total}",
            //    ShaderCacheState => $"Shaders : {current}/{total}",
            //    _ => throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}"),
            // };

            string jsonData = state switch
            {
                LoadState => $"[\"PTC\",{current},{total}]",
                ShaderCacheState => $"[\"Shaders\",{current},{total}]",
                _ => throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}"),
            };

            // Convert to UTF-8 bytes
            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonData);

            // Allocate unmanaged memory and send
            IntPtr unmanagedPointer = Marshal.AllocHGlobal(jsonBytes.Length);
            try
            {
                Marshal.Copy(jsonBytes, 0, unmanagedPointer, jsonBytes.Length);

                TriggerCallbackWithData(
                    "ProgressWithPTCorShaderCache",
                    unmanagedPointer,
                    (UIntPtr)jsonBytes.Length
                );
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedPointer);
            }

            // Logger.Info?.Print(LogClass.Application, label);
        }

        private static WindowBase CreateWindow(Options options)
        {
            if (OperatingSystem.IsIOS()) {
                return new MoltenVKWindow(_inputManager, options.LoggingGraphicsDebugLevel, options.AspectRatio, options.EnableMouse, options.HideCursorMode);
            }
            else 
            {
                return options.GraphicsBackend == GraphicsBackend.Vulkan
                    ? new VulkanWindow(_inputManager, options.LoggingGraphicsDebugLevel, options.AspectRatio, options.EnableMouse, options.HideCursorMode)
                    : new OpenGLWindow(_inputManager, options.LoggingGraphicsDebugLevel, options.AspectRatio, options.EnableMouse, options.HideCursorMode);
            }
        }

        private static IRenderer CreateRenderer(Options options, WindowBase window)
        {
            if (options.GraphicsBackend == GraphicsBackend.Vulkan)
            {
                string preferredGpuId = string.Empty;
                Vk api = Vk.GetApi();

                // Handle GPU preference selection
                if (!string.IsNullOrEmpty(options.PreferredGPUVendor))
                {
                    string preferredGpuVendor = options.PreferredGPUVendor.ToLowerInvariant();
                    var devices = VulkanRenderer.GetPhysicalDevices(api);

                    foreach (var device in devices)
                    {
                        if (device.Vendor.ToLowerInvariant() == preferredGpuVendor)
                        {
                            preferredGpuId = device.Id;
                            break;
                        }
                    }
                }

                if (window is VulkanWindow vulkanWindow)
                {
                    return new VulkanRenderer(
                        api,
                        (instance, vk) => new SurfaceKHR((ulong)(vulkanWindow.CreateWindowSurface(instance.Handle))),
                        vulkanWindow.GetRequiredInstanceExtensions,
                        preferredGpuId);
                }
                else if (window is MoltenVKWindow mvulkanWindow)
                {
                    return new VulkanRenderer(
                        api,
                        (instance, vk) => new SurfaceKHR((ulong)(mvulkanWindow.CreateWindowSurface(instance.Handle))),
                        mvulkanWindow.GetRequiredInstanceExtensions,
                        preferredGpuId);
                }
            }

            // Fallback to OpenGL renderer if Vulkan is not used
            return new OpenGLRenderer();
        }

        private static Switch InitializeEmulationContext(WindowBase window, IRenderer renderer, Options options)
        {
            BackendThreading threadingMode = options.BackendThreading;

            bool AppleHV = false;

            if ((!OperatingSystem.IsIOSVersionAtLeast(16, 4)) && options.UseHypervisor) 
            {
                AppleHV = true;
            }
            else if (OperatingSystem.IsIOS()) 
            {
                AppleHV = false;
            } else {
                AppleHV = options.UseHypervisor;
            }

            // idk :3
            HLE.HOS.Services.Nifm.StaticService.AnyInternetRequestAccepted.isAnyInternetRequestAccepted = options.EnableInternetAccess;

            HLEConfiguration configuration = new(_virtualFileSystem,
                _libHacHorizonManager,
                _contentManager,
                _accountManager,
                _userChannelPersistence,
                renderer,
                new SDL2HardwareDeviceDriver(), // AppleHardwareDeviceDriver(), // Will rework Apple Audio later.
                options.ExpandRAM ? MemoryConfiguration.MemoryConfiguration8GiB : MemoryConfiguration.MemoryConfiguration4GiB,
                window,
                options.SystemLanguage,
                options.SystemRegion,
                !options.DisableVSync,
                !options.DisableDockedMode,
                !options.DisablePTC,
                options.EnableInternetAccess,
                !options.DisableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                options.FsGlobalAccessLogMode,
                options.SystemTimeOffset,
                options.SystemTimeZone,
                options.MemoryManagerMode,
                options.IgnoreMissingServices,
                options.AspectRatio,
                options.AudioVolume,
                AppleHV,
                options.MultiplayerLanInterfaceId,
                options.ldnMitm ? MultiplayerMode.LdnMitm : MultiplayerMode.Disabled);

            return new Switch(configuration);
        }

        private static bool RunCleanupStep(string name, Action action, ref ExceptionDispatchInfo failure)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception exception)
            {
                if (failure == null)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                else
                {
                    Logger.Error?.Print(LogClass.Application, $"Additional failure while cleaning up {name}: {exception}");
                }

                return false;
            }
        }

        private static bool RunGpuCleanupThread(string name, Action action, ref ExceptionDispatchInfo failure)
        {
            ExceptionDispatchInfo gpuFailure = null;
            Thread cleanupThread = new(() =>
            {
                Interlocked.Increment(ref WindowBase.ActiveRenderThreadCount);

                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    gpuFailure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    Interlocked.Decrement(ref WindowBase.ActiveRenderThreadCount);
                }
            })
            {
                Name = "GUI.RenderLoop.Cleanup",
            };

            bool threadCompleted = RunCleanupStep(name, () =>
            {
                cleanupThread.Start();
                cleanupThread.Join();
            }, ref failure);

            if (gpuFailure != null)
            {
                if (failure == null)
                {
                    failure = gpuFailure;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Application, $"Additional failure while disposing the GPU: {gpuFailure.SourceException}");
                }
            }

            return threadCompleted && gpuFailure == null;
        }

        private static void ShutdownEmulationInstance(
            EmulationSession session,
            WindowBase window,
            Switch emulationContext,
            IRenderer renderer)
        {
            ExceptionDispatchInfo cleanupFailure = null;
            ExceptionDispatchInfo executionFailure = null;
            session.MarkStopRequested();
            session.CloseControlAccess();

            if (window != null)
            {
                RunCleanupStep("GPU producer", window.StopAndWaitForGpu, ref cleanupFailure);
            }

            session.MarkGpuProducerDone();

            if (emulationContext != null)
            {
                RunCleanupStep("emulation context", emulationContext.Dispose, ref cleanupFailure);
            }

            session.MarkSystemDisposed();
            session.MarkGpuDisposing();

            bool renderJoined = false;

            if (emulationContext != null)
            {
                if (window?.Device != null)
                {
                    RunCleanupStep("renderer stop", window.RequestRendererStopAfterDrain, ref cleanupFailure);
                    renderJoined = RunCleanupStep("renderer thread", window.WaitForRendererExit, ref cleanupFailure);
                    RunCleanupStep("renderer failure propagation", window.ThrowIfRenderFailed, ref executionFailure);
                    RunCleanupStep(
                        "renderer GPU disposal",
                        window.ThrowIfRendererDisposeFailed,
                        ref cleanupFailure);
                }
                else
                {
                    renderJoined = RunGpuCleanupThread("GPU cleanup thread", () =>
                    {
                        session.LogMilestone("GpuDisposeStart");
                        emulationContext.DisposeGpu();
                        session.LogMilestone("GpuDisposed");
                    }, ref cleanupFailure);
                }
            }
            else if (renderer != null)
            {
                renderJoined = RunGpuCleanupThread("unowned renderer", () =>
                {
                    session.LogMilestone("GpuDisposeStart");
                    renderer.Dispose();
                    session.LogMilestone("GpuDisposed");
                }, ref cleanupFailure);
            }

            if (renderJoined)
            {
                session.LogMilestone("RenderJoined");
            }

            if (window != null)
            {
                RunCleanupStep("window", window.Dispose, ref cleanupFailure);
            }

            RunCleanupStep("display sleep state", DisplaySleep.Restore, ref cleanupFailure);
            RunCleanupStep("timer resolution", () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    _windowsMultimediaTimerResolution?.Dispose();
                }

                _windowsMultimediaTimerResolution = null;
            }, ref cleanupFailure);

            session.LogMilestone("ApplicationCycleDisposed");

            if (executionFailure != null)
            {
                session.RecordFault(executionFailure.SourceException);
            }

            if (cleanupFailure != null)
            {
                session.RecordTeardownFault(cleanupFailure.SourceException);
                cleanupFailure.Throw();
            }

            executionFailure?.Throw();
        }

        private static void ExecutionEntrypoint(
            EmulationSession session,
            WindowBase window,
            Switch emulationContext,
            IRenderer renderer)
        {
            ExceptionDispatchInfo failure = null;

            try
            {
                if (!session.IsStopRequested)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _windowsMultimediaTimerResolution = new WindowsMultimediaTimerResolution(1);
                    }

                    DisplaySleep.Prevent();

                    window.Initialize(emulationContext, _inputConfiguration, _enableKeyboard, _enableMouse);
                    session.MarkRunning();

                    if (!session.IsStopRequested)
                    {
                        window.Execute();
                    }
                }
            }
            catch (OperationCanceledException) when (session.IsStopRequested)
            {
            }
            catch (Exception exception)
            {
                session.RecordFault(exception);
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                try
                {
                    ShutdownEmulationInstance(session, window, emulationContext, renderer);
                }
                catch (Exception exception)
                {
                    session.RecordFault(exception);

                    if (failure == null)
                    {
                        failure = ExceptionDispatchInfo.Capture(exception);
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.Application, $"Additional failure during emulation shutdown: {exception}");
                    }
                }
            }

            failure?.Throw();
        }

        private static bool LoadApplication(Options options, EmulationSession session)
        {
            string path = options.InputPath;
            WindowBase window = null;
            IRenderer renderer = null;
            Switch emulationContext = null;
            bool executionOwnsCleanup = false;

            Logger.RestartTime();

            try
            {
                try
                {
                    window = CreateWindow(options);
                }
                catch (WindowConstructionRollbackException exception)
                {
                    session.RecordTeardownFault(exception);
                    throw;
                }

                session.AttachWindow(window);

                if (window is MoltenVKWindow mvulkanWindow)
                {
                    mvulkanWindow.SetNativeWindow(session.GetNativeMetalLayer());
                }

                renderer = CreateRenderer(options, window);
                session.TrackRenderer(renderer);

                window.IsFullscreen = options.IsFullscreen;
                window.DisplayId = options.DisplayId;
                window.IsExclusiveFullscreen = options.IsExclusiveFullscreen;
                window.ExclusiveFullscreenWidth = options.ExclusiveFullscreenWidth;
                window.ExclusiveFullscreenHeight = options.ExclusiveFullscreenHeight;
                window.AntiAliasing = options.AntiAliasing;
                window.ScalingFilter = options.ScalingFilter;
                window.ScalingFilterLevel = options.ScalingFilterLevel;
                renderer.Window?.SetColorSpacePassthrough(true);
                renderer = new ThreadedRenderer(renderer);
                session.TrackRenderer(renderer);

                try
                {
                    emulationContext = InitializeEmulationContext(window, renderer, options);
                }
                catch (SwitchConstructionRollbackException exception)
                {
                    session.RecordTeardownFault(exception);
                    throw;
                }

                session.AttachEmulationContext(emulationContext);

                if (session.IsStopRequested)
                {
                    return false;
                }

                SystemVersion firmwareVersion = _contentManager.GetCurrentFirmwareVersion();

                Logger.Notice.Print(LogClass.Application, $"Using Firmware Version: {firmwareVersion?.VersionString}");

                bool isFirmwareTitle = false;

                if (path.StartsWith("@SystemContent"))
                {
                    path = VirtualFileSystem.SwitchPathToSystemPath(path);

                    isFirmwareTitle = true;
                }

                if (Directory.Exists(path))
                {
                    string[] romFsFiles = Directory.GetFiles(path, "*.istorage");

                    if (romFsFiles.Length == 0)
                    {
                        romFsFiles = Directory.GetFiles(path, "*.romfs");
                    }

                    if (romFsFiles.Length > 0)
                    {
                        Logger.Info?.Print(LogClass.Application, "Loading as cart with RomFS.");

                        if (!emulationContext.LoadCart(path, romFsFiles[0]))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Application, "Loading as cart WITHOUT RomFS.");

                        if (!emulationContext.LoadCart(path))
                        {
                            return false;
                        }
                    }
                }
                else if (File.Exists(path))
                {
                    switch (Path.GetExtension(path).ToLowerInvariant())
                    {
                        case ".xci":
                            Logger.Info?.Print(LogClass.Application, "Loading as XCI.");

                            if (!emulationContext.LoadXci(path))
                            {
                                return false;
                            }
                            break;
                        case ".nca":
                            Logger.Info?.Print(LogClass.Application, "Loading as NCA.");

                            if (!emulationContext.LoadNca(path))
                            {
                                return false;
                            }
                            break;
                        case ".nsp":
                        case ".pfs0":
                            Logger.Info?.Print(LogClass.Application, "Loading as NSP.");

                            if (!emulationContext.LoadNsp(path))
                            {
                                return false;
                            }
                            break;
                        default:
                            if (isFirmwareTitle)
                            {
                                Logger.Info?.Print(LogClass.Application, "Loading as Firmware Title (NCA).");

                                if (!emulationContext.LoadNca(path))
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                Logger.Info?.Print(LogClass.Application, "Loading as Homebrew.");
                                try
                                {
                                    if (!emulationContext.LoadProgram(path))
                                    {
                                        return false;
                                    }
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    Logger.Error?.Print(LogClass.Application, "The specified file is not supported by Ryujinx.");

                                    return false;
                                }
                            }
                            break;
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Application, $"Couldn't load '{options.InputPath}'. Please specify a valid XCI/NCA/NSP/PFS0/NRO file.");

                    return false;
                }

                if (session.IsStopRequested)
                {
                    return false;
                }

                SetupProgressHandler(emulationContext);
                executionOwnsCleanup = true;
                ExecutionEntrypoint(session, window, emulationContext, renderer);

                return true;
            }
            finally
            {
                if (!executionOwnsCleanup)
                {
                    ShutdownEmulationInstance(session, window, emulationContext, renderer);
                }
            }
        }

        private static FileStream OpenFile(int descriptor)
        {
            var safeHandle = new SafeFileHandle(descriptor, false);

            return new FileStream(safeHandle, FileAccess.ReadWrite);
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct AvatarInfo
        {
            public byte* ImageData;    
            public int ImageSize;     
            public sbyte* FileName;  
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct AvatarArray
        {
            public int Count;          
            public AvatarInfo* Avatars; 
        }

        public class GameInfo
        {
            public double FileSize;
            public string? TitleName;
            public string? TitleId;
            public string? Developer;
            public string? Version;
            public byte[]? Icon;
        }

        public unsafe struct DlcNcaListItem 
        {
            public fixed byte Path[256];
            public ulong TitleId;
        }

        public unsafe struct DlcNcaList
        {
            public bool success;
            public uint size;
            public unsafe DlcNcaListItem* items;

        }

        public unsafe struct GameInfoNative
        {
            public ulong FileSize;
            public fixed byte TitleName[512];
            public fixed byte TitleId[32];
            public fixed byte Developer[256];
            public fixed byte Version[16];  
            public byte* ImageData;  
            public uint ImageSize;   

            public GameInfoNative(ulong fileSize, string titleName, string titleId, string developer, string version, byte[] imageData)
            {
                FileSize = fileSize;

                fixed (byte* titleNamePtr = TitleName)
                fixed (byte* titleIdPtr = TitleId)
                fixed (byte* developerPtr = Developer)
                fixed (byte* versionPtr = Version)
                {
                    CopyStringToFixedArray(titleName, titleNamePtr, 512);
                    CopyStringToFixedArray(titleId, titleIdPtr, 32);
                    CopyStringToFixedArray(developer, developerPtr, 256);
                    CopyStringToFixedArray(version, versionPtr, 16);
                }

                if (imageData == null || imageData.Length > 4096 * 4096)
                {
                    ImageSize = 0;
                    ImageData = null;
                }
                else 
                {
                    ImageSize = (uint)imageData.Length;
                    ImageData = (byte*)Marshal.AllocHGlobal(imageData.Length);
                    Marshal.Copy(imageData, 0, (IntPtr)ImageData, imageData.Length);
                }
            }

            public void Dispose()
            {
                if (ImageData != null)
                {
                    Marshal.FreeHGlobal((IntPtr)ImageData);
                    ImageData = null;
                }
            }
        }
        
        [UnmanagedCallersOnly(EntryPoint = "free_game_info")]
        public static unsafe void FreeGameInfo(GameInfoNative* gameInfoPtr)
        {
            if (gameInfoPtr == null)
                return;
                
            if (gameInfoPtr->ImageData != null)
            {
                Marshal.FreeHGlobal((IntPtr)gameInfoPtr->ImageData);
                gameInfoPtr->ImageData = null;
            }
            gameInfoPtr->ImageSize = 0;
        }

        private static unsafe void CopyStringToFixedArray(string source, byte* destination, int length)
        {
            var span = new Span<byte>(destination, length);
            span.Clear();
            Encoding.UTF8.GetBytes(source, span);
        }

        [UnmanagedCallersOnly(EntryPoint = "update_settings_external")]
        public static unsafe int UpdateSettingsExternal(int argCount, IntPtr* pArgs)
        {
            string[] args = new string[argCount];

            try
            {
                for (int i = 0; i < argCount; i++)
                {
                    args[i] = Marshal.PtrToStringAnsi(pArgs[i]);
                }

                Options parsedOptions = null;
                Parser.Default.ParseArguments<Options>(args)
                    .WithParsed(opts => parsedOptions = opts);

                if (parsedOptions == null)
                {
                    Console.WriteLine("Failed to parse options.");
                    return -1;
                }

                ApplyDynamicSettings(parsedOptions);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return -1;
            }

            return 0;
        }

        private static void ApplyDynamicSettings(Options options)
        {
            GraphicsConfig.ResScale = options.ResScale;
            GraphicsConfig.MaxAnisotropy = options.MaxAnisotropy;
            GraphicsConfig.EnableShaderCache = !options.DisableShaderCache;
            GraphicsConfig.EnableTextureRecompression = options.EnableTextureRecompression;
            GraphicsConfig.EnableMacroHLE = !options.DisableMacroHLE;

            Volatile.Read(ref _currentSession)?.TryUseEmulationContext(emulationContext =>
            {
                emulationContext.SetVolume(options.AudioVolume);
                emulationContext.System.State.SetLanguage(options.SystemLanguage);
                emulationContext.System.State.SetRegion(options.SystemRegion);
                emulationContext.EnableDeviceVsync = !options.DisableVSync;
                emulationContext.System.State.DockedMode = !options.DisableDockedMode;
                emulationContext.System.EnablePtc = !options.DisablePTC;
                emulationContext.System.FsIntegrityCheckLevel = !options.DisableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None;
                emulationContext.System.GlobalAccessLogMode = options.FsGlobalAccessLogMode;
                emulationContext.Configuration.IgnoreMissingServices = options.IgnoreMissingServices;
                emulationContext.Configuration.AspectRatio = options.AspectRatio;
                emulationContext.Configuration.EnableInternetAccess = options.EnableInternetAccess;
                emulationContext.Configuration.MemoryManagerMode = options.MemoryManagerMode;
                emulationContext.Configuration.MultiplayerLanInterfaceId = options.MultiplayerLanInterfaceId;
            });
        }
    }


}
