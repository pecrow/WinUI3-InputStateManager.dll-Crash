using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Change this namespace to match your project
namespace YourAppNamespace
{
    /// <summary>
    /// Throttles rapid WM_POINTER messages at the Win32 level to work around
    /// a crash in Microsoft.InputStateManager.dll (PopulateContactInFrame)
    /// where rapid touch input causes __fastfail via GetRawPointerDeviceData.
    /// See: https://github.com/microsoft/microsoft-ui-xaml/issues/10929
    /// </summary>
    internal static class InputThrottleHelper
    {
        private const uint WM_POINTERUPDATE = 0x0245;
        private const uint WM_POINTERDOWN   = 0x0246;
        private const uint WM_POINTERUP     = 0x0247;

        private const nuint SubclassId = 0x5444; // "TD"

        // Minimum ms between WM_POINTERUPDATE messages per pointer ID.
        // 4 ms ≈ 250 Hz cap — imperceptible, prevents semaphore overload.
        private const int MinUpdateIntervalMs = 4;

        // Max simultaneous touch contacts forwarded to WinUI 3 (0 = unlimited).
        // Contacts beyond this limit are silently dropped at the Win32 level.
        private const int MaxConcurrentPointers = 1;

        private static readonly Dictionary<uint, long> _lastUpdateTick = new();
        private static readonly HashSet<uint> _activePointers = new();
        private static readonly HashSet<IntPtr> _installedHwnds = new();
        private static SubclassProc? _subclassProc;

        private static uint GetPointerId(nuint wParam) => (uint)(wParam & 0xFFFF);

        /// <summary>
        /// Installs the pointer-throttling subclass on the given window.
        /// Safe to call on multiple HWNDs — pointer IDs are system-wide unique.
        /// </summary>
        public static void Install(IntPtr hwnd)
        {
            _subclassProc ??= WndProc;
            if (SetWindowSubclass(hwnd, _subclassProc, SubclassId, 0))
                _installedHwnds.Add(hwnd);
        }

        /// <summary>
        /// Removes the subclass from the given window.
        /// </summary>
        public static void Uninstall(IntPtr hwnd)
        {
            if (_subclassProc != null && _installedHwnds.Remove(hwnd))
                RemoveWindowSubclass(hwnd, _subclassProc, SubclassId);
        }

        private static nint WndProc(
            IntPtr hWnd, uint uMsg, nuint wParam, nint lParam,
            nuint uIdSubclass, nuint dwRefData)
        {
            switch (uMsg)
            {
                case WM_POINTERDOWN:
                {
                    uint id = GetPointerId(wParam);
                    if (MaxConcurrentPointers > 0
                        && _activePointers.Count >= MaxConcurrentPointers
                        && !_activePointers.Contains(id))
                        return 0; // over the limit — swallow
                    _activePointers.Add(id);
                    _lastUpdateTick[id] = Environment.TickCount64;
                    break;
                }

                case WM_POINTERUP:
                {
                    uint id = GetPointerId(wParam);
                    if (!_activePointers.Remove(id))
                        return 0; // pointer was never admitted
                    _lastUpdateTick.Remove(id);
                    break;
                }

                case WM_POINTERUPDATE:
                {
                    uint id = GetPointerId(wParam);
                    if (!_activePointers.Contains(id))
                        return 0; // unknown / rejected pointer

                    long now = Environment.TickCount64;
                    if (_lastUpdateTick.TryGetValue(id, out long last)
                        && (now - last) < MinUpdateIntervalMs)
                        return 0; // too fast — drop this frame

                    _lastUpdateTick[id] = now;
                    break;
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        // ── P/Invoke ─────────────────────────────────────────────

        private delegate nint SubclassProc(
            IntPtr hWnd, uint uMsg, nuint wParam, nint lParam,
            nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern nint DefSubclassProc(
            IntPtr hWnd, uint uMsg, nuint wParam, nint lParam);
    }
}
