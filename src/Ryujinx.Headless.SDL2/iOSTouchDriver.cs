using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

namespace Ryujinx.Headless.SDL2
{
    class iOSTouchDriver : IGamepadDriver
    {
        private const int CursorHideIdleTime = 5;

        private int _isDisposed;
        private readonly HideCursorMode _hideCursorMode;
        private readonly object _activeTouchesLock = new();
        private readonly Dictionary<int, Vector2> _activeTouches = new();
        private bool _isHidden;
        private long _lastCursorMoveTime;

        public bool[] PressedButtons { get; }
        public Vector2[] CurrentPosition
        {
            get
            {
                lock (_activeTouchesLock)
                {
                    return _activeTouches.Values.ToArray();
                }
            }
        }
        public Vector2 Scroll { get; private set; }
        public Size ClientSize;

        private static iOSTouchDriver _currentDriver;

        public iOSTouchDriver(HideCursorMode hideCursorMode)
        {
            PressedButtons = new bool[(int)MouseButton.Count];
            _hideCursorMode = hideCursorMode;
            Interlocked.Exchange(ref _currentDriver, this);
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_began")]
        public static void TouchBeganAtPoint(float x, float y, int index)
        {
            try
            {
                Volatile.Read(ref _currentDriver)?.TouchBegan(x, y, index);
            }
            catch
            {
                // Exceptions must never cross an UnmanagedCallersOnly boundary.
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_moved")]
        public static void TouchMovedAtPoint(float x, float y, int index)
        {
            try
            {
                Volatile.Read(ref _currentDriver)?.TouchMoved(x, y, index);
            }
            catch
            {
                // Exceptions must never cross an UnmanagedCallersOnly boundary.
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "touch_ended")]
        public static void TouchEndedForIndex(int index)
        {
            try
            {
                Volatile.Read(ref _currentDriver)?.TouchEnded(index);
            }
            catch
            {
                // Exceptions must never cross an UnmanagedCallersOnly boundary.
            }
        }

        private void TouchBegan(float x, float y, int index)
        {
            lock (_activeTouchesLock)
            {
                if (Volatile.Read(ref _isDisposed) == 0)
                {
                    _activeTouches[index] = new Vector2(x, y);
                }
            }
        }

        private void TouchMoved(float x, float y, int index)
        {
            lock (_activeTouchesLock)
            {
                if (Volatile.Read(ref _isDisposed) == 0 && _activeTouches.ContainsKey(index))
                {
                    _activeTouches[index] = new Vector2(x, y);
                }
            }
        }

        private void TouchEnded(int index)
        {
            lock (_activeTouchesLock)
            {
                if (Volatile.Read(ref _isDisposed) == 0)
                {
                    _activeTouches.Remove(index);
                }
            }
        }

        public void UpdatePosition()
        {
            lock (_activeTouchesLock)
            {
                if (_activeTouches.Count > 0)
                {
                    var touch = _activeTouches.Values.GetEnumerator();
                    touch.MoveNext();
                    Vector2 position = touch.Current;
                }
            }

            CheckIdle();
        }

        private void CheckIdle()
        {
            if (_hideCursorMode != HideCursorMode.OnIdle)
            {
                return;
            }

            long cursorMoveDelta = Stopwatch.GetTimestamp() - _lastCursorMoveTime;

            if (cursorMoveDelta >= CursorHideIdleTime * Stopwatch.Frequency)
            {
                if (!_isHidden)
                {
                    Logger.Debug?.Print(LogClass.Application, "Hiding cursor due to inactivity.");
                    _isHidden = true;
                }
            }
            else
            {
                if (_isHidden)
                {
                    Logger.Debug?.Print(LogClass.Application, "Showing cursor after activity.");
                    _isHidden = false;
                }
            }
        }


        public void SetClientSize(int width, int height)
        {
            ClientSize = new Size(width, height);
        }

        public bool IsAnyPressed(MouseButton button)
        {
            lock (_activeTouchesLock)
            {
                return _activeTouches.Count > 0;
            }
        }

        public Size GetClientSize()
        {
            return ClientSize;
        }

        public string DriverName => "iOSTouchDriver";

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public ReadOnlySpan<string> GamepadsIds => new[] { "0" };

        public IGamepad GetGamepad(string id)
        {
            return new iOSMouse(this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            Interlocked.CompareExchange(ref _currentDriver, null, this);

            lock (_activeTouchesLock)
            {
                _activeTouches.Clear();
            }
        }
    }
}
