using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.HLE.HOS.Services.Bluetooth.BluetoothDriver
{
    static class BluetoothEventManager
    {
        internal static object SyncRoot { get; } = new();

        public static KEvent InitializeBleDebugEvent;
        public static int InitializeBleDebugEventHandle;

        public static KEvent UnknownBleDebugEvent;
        public static int UnknownBleDebugEventHandle;

        public static KEvent RegisterBleDebugEvent;
        public static int RegisterBleDebugEventHandle;

        public static KEvent InitializeBleEvent;
        public static int InitializeBleEventHandle;

        public static KEvent UnknownBleEvent;
        public static int UnknownBleEventHandle;

        public static KEvent RegisterBleEvent;
        public static int RegisterBleEventHandle;

        internal static void Reset()
        {
            lock (SyncRoot)
            {
                InitializeBleDebugEvent = null;
                InitializeBleDebugEventHandle = 0;
                UnknownBleDebugEvent = null;
                UnknownBleDebugEventHandle = 0;
                RegisterBleDebugEvent = null;
                RegisterBleDebugEventHandle = 0;

                InitializeBleEvent = null;
                InitializeBleEventHandle = 0;
                UnknownBleEvent = null;
                UnknownBleEventHandle = 0;
                RegisterBleEvent = null;
                RegisterBleEventHandle = 0;
            }
        }
    }
}
