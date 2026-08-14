using NUnit.Framework;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Bluetooth.BluetoothDriver;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;
using Ryujinx.HLE.HOS.Services.Time;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Tests.HLE
{
    [TestFixture]
    [NonParallelizable]
    internal class SessionStaticResourceTests
    {
        [TearDown]
        public void ResetBluetoothState()
        {
            BluetoothEventManager.Reset();
            BsdContext.ReleaseAll();
            NvMapDeviceFile.Destroy();
        }

        [Test]
        public void TimeManagerReleaseSessionClearsSessionResourcesAndIsIdempotent()
        {
            (
                TimeManager manager,
                WeakReference device,
                WeakReference sharedMemory,
                WeakReference storage,
                WeakReference autoCorrectionEvent) = CreateAndReleaseTimeSession();

            Assert.That(manager, Is.Not.Null);

            ForceCollection();

            Assert.That(device.IsAlive, Is.False);
            Assert.That(sharedMemory.IsAlive, Is.False);
            Assert.That(storage.IsAlive, Is.False);
            Assert.That(autoCorrectionEvent.IsAlive, Is.False);
        }

        [Test]
        public void OldTimeSessionCannotReleaseNewSession()
        {
            TimeManager manager = new();
            Switch oldSession = CreateUninitialized<Switch>();
            Switch currentSession = CreateUninitialized<Switch>();
            KSharedMemory sharedMemory = CreateUninitialized<KSharedMemory>();
            SharedMemoryStorage storage = CreateUninitialized<SharedMemoryStorage>();
            KEvent autoCorrectionEvent = CreateUninitialized<KEvent>();

            SeedTimeSession(
                manager,
                currentSession,
                sharedMemory,
                storage,
                autoCorrectionEvent);

            Assert.That(manager.ReleaseSession(oldSession), Is.False);
            Assert.That(GetField<Switch>(manager, "_sessionDevice"), Is.SameAs(currentSession));
            Assert.That(GetField<Switch>(manager.SharedMemory, "_device"), Is.SameAs(currentSession));
            Assert.That(GetField<KSharedMemory>(manager.SharedMemory, "_sharedMemory"), Is.SameAs(sharedMemory));
            Assert.That(GetField<SharedMemoryStorage>(manager.SharedMemory, "_timeSharedMemoryStorage"), Is.SameAs(storage));
            Assert.That(GetField<KEvent>(manager.StandardUserSystemClock, "_autoCorrectionEvent"), Is.SameAs(autoCorrectionEvent));

            Assert.That(manager.ReleaseSession(currentSession), Is.True);
            Assert.That(manager.ReleaseSession(currentSession), Is.False);
        }

        [Test]
        public void BluetoothResetClearsAllEventsAndHandlesAndIsIdempotent()
        {
            WeakReference eventReference = SeedAndResetBluetoothState();

            AssertBluetoothStateIsEmpty();

            BluetoothEventManager.Reset();
            AssertBluetoothStateIsEmpty();

            ForceCollection();
            Assert.That(eventReference.IsAlive, Is.False);
        }

        [Test]
        public void BsdReleaseAllDisposesSharedDescriptorsOnceAndClearsRegistry()
        {
            BsdContext context = BsdContext.GetOrRegister(123);
            TestFileDescriptor descriptor = new() { Refcount = 1 };

            int firstFd = context.RegisterFileDescriptor(descriptor);
            int secondFd = context.DuplicateFileDescriptor(firstFd);

            Assert.That(secondFd, Is.Not.EqualTo(-1));
            Assert.That(descriptor.Refcount, Is.EqualTo(2));

            BsdContext.ReleaseAll();

            Assert.That(descriptor.DisposeCount, Is.EqualTo(1));
            Assert.That(BsdContext.GetContext(123), Is.Null);

            BsdContext.ReleaseAll();
            Assert.That(descriptor.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void NvMapDestroyClearsStaticHandlesAndIsIdempotent()
        {
            FieldInfo mapsField = typeof(NvMapDeviceFile).GetField("_maps", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(NvMapDeviceFile).FullName, "_maps");
            NvMapIdDictionary maps = (NvMapIdDictionary)mapsField.GetValue(null);

            maps.Add(new NvMapHandle(0x1000));
            Assert.That(maps.Values, Is.Not.Empty);

            NvMapDeviceFile.Destroy();
            Assert.That(maps.Values, Is.Empty);

            NvMapDeviceFile.Destroy();
            Assert.That(maps.Values, Is.Empty);
        }

        [Test]
        public void SurfaceFlingerDisposeJoinsComposerAndIsIdempotent()
        {
            SurfaceFlinger surfaceFlinger = new(CreateUninitialized<Switch>());
            Thread composerThread = GetField<Thread>(surfaceFlinger, "_composerThread");

            Assert.That(composerThread.IsAlive, Is.True);

            surfaceFlinger.RequestStop();
            surfaceFlinger.Dispose();

            Assert.That(composerThread.IsAlive, Is.False);
            Assert.DoesNotThrow(surfaceFlinger.Dispose);
            Assert.DoesNotThrow(surfaceFlinger.RequestStop);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (
            TimeManager Manager,
            WeakReference Device,
            WeakReference SharedMemory,
            WeakReference Storage,
            WeakReference AutoCorrectionEvent) CreateAndReleaseTimeSession()
        {
            TimeManager manager = new();
            Switch device = CreateUninitialized<Switch>();
            KSharedMemory sharedMemory = CreateUninitialized<KSharedMemory>();
            SharedMemoryStorage storage = CreateUninitialized<SharedMemoryStorage>();
            KEvent autoCorrectionEvent = CreateUninitialized<KEvent>();

            SeedTimeSession(manager, device, sharedMemory, storage, autoCorrectionEvent);

            WeakReference deviceReference = new(device);
            WeakReference sharedMemoryReference = new(sharedMemory);
            WeakReference storageReference = new(storage);
            WeakReference eventReference = new(autoCorrectionEvent);

            Assert.That(manager.ReleaseSession(device), Is.True);
            Assert.That(manager.ReleaseSession(device), Is.False);

            return (
                manager,
                deviceReference,
                sharedMemoryReference,
                storageReference,
                eventReference);
        }

        private static void SeedTimeSession(
            TimeManager manager,
            Switch device,
            KSharedMemory sharedMemory,
            SharedMemoryStorage storage,
            KEvent autoCorrectionEvent)
        {
            SetField(manager, "_sessionDevice", device);
            SetField(manager.SharedMemory, "_device", device);
            SetField(manager.SharedMemory, "_sharedMemory", sharedMemory);
            SetField(manager.SharedMemory, "_timeSharedMemoryStorage", storage);
            SetField(manager.SharedMemory, "_timeSharedMemorySize", 0x1000);
            SetField(manager.StandardUserSystemClock, "_autoCorrectionEvent", autoCorrectionEvent);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference SeedAndResetBluetoothState()
        {
            KEvent eventObject = CreateUninitialized<KEvent>();
            WeakReference eventReference = new(eventObject);

            BluetoothEventManager.InitializeBleDebugEvent = eventObject;
            BluetoothEventManager.InitializeBleDebugEventHandle = 1;
            BluetoothEventManager.UnknownBleDebugEvent = eventObject;
            BluetoothEventManager.UnknownBleDebugEventHandle = 2;
            BluetoothEventManager.RegisterBleDebugEvent = eventObject;
            BluetoothEventManager.RegisterBleDebugEventHandle = 3;
            BluetoothEventManager.InitializeBleEvent = eventObject;
            BluetoothEventManager.InitializeBleEventHandle = 4;
            BluetoothEventManager.UnknownBleEvent = eventObject;
            BluetoothEventManager.UnknownBleEventHandle = 5;
            BluetoothEventManager.RegisterBleEvent = eventObject;
            BluetoothEventManager.RegisterBleEventHandle = 6;

            BluetoothEventManager.Reset();

            return eventReference;
        }

        private static void AssertBluetoothStateIsEmpty()
        {
            Assert.That(BluetoothEventManager.InitializeBleDebugEvent, Is.Null);
            Assert.That(BluetoothEventManager.InitializeBleDebugEventHandle, Is.Zero);
            Assert.That(BluetoothEventManager.UnknownBleDebugEvent, Is.Null);
            Assert.That(BluetoothEventManager.UnknownBleDebugEventHandle, Is.Zero);
            Assert.That(BluetoothEventManager.RegisterBleDebugEvent, Is.Null);
            Assert.That(BluetoothEventManager.RegisterBleDebugEventHandle, Is.Zero);
            Assert.That(BluetoothEventManager.InitializeBleEvent, Is.Null);
            Assert.That(BluetoothEventManager.InitializeBleEventHandle, Is.Zero);
            Assert.That(BluetoothEventManager.UnknownBleEvent, Is.Null);
            Assert.That(BluetoothEventManager.UnknownBleEventHandle, Is.Zero);
            Assert.That(BluetoothEventManager.RegisterBleEvent, Is.Null);
            Assert.That(BluetoothEventManager.RegisterBleEventHandle, Is.Zero);
        }

        private static T CreateUninitialized<T>() where T : class
        {
            return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)GetField(target.GetType(), name).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            GetField(target.GetType(), name).SetValue(target, value);
        }

        private static FieldInfo GetField(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(type.FullName, name);
        }

        private static void ForceCollection()
        {
            for (int index = 0; index < 3; index++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private sealed class TestFileDescriptor : IFileDescriptor
        {
            public bool Blocking { get; set; }
            public int Refcount { get; set; }
            public int DisposeCount { get; private set; }

            public LinuxError Read(out int readSize, Span<byte> buffer)
            {
                readSize = 0;
                return LinuxError.SUCCESS;
            }

            public LinuxError Write(out int writeSize, ReadOnlySpan<byte> buffer)
            {
                writeSize = buffer.Length;
                return LinuxError.SUCCESS;
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
