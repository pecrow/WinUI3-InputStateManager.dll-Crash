# WinUI 3 `InputStateManager.dll` Crash — Root Cause Analysis & Workaround

**Affects ALL WinUI 3 / Windows App SDK applications using touch input.**

Microsoft Issue: [microsoft-ui-xaml#10929](https://github.com/microsoft/microsoft-ui-xaml/issues/10929)

I build this workaround because it was affecting My Application:
Touch Dial - https://ramirez.cr/TouchDial/

---

## The Problem

WinUI 3 applications crash with an uncatchable fail-fast exception (`0xC0000409 STATUS_STACK_BUFFER_OVERRUN`) whenever touch input is used. The crash originates from `Microsoft.InputStateManager.dll`, specifically the function `InProcInputHandler::PopulateContactInFrame`.

- **Trigger:** Rapid multi-finger touch input (4+ fingers, tapping quickly)
- **Hardware sensitivity:** Slower devices (e.g., Surface Pro tablets) crash within seconds; faster desktops may take minutes
- **Affected SDK versions:** WinAppSDK 1.6.x, 1.7.x, 1.8.x (1.6.x is less frequent)
- **Scope:** Affects ALL WinUI 3 applications, including blank template apps with zero custom code

### Why Other Frameworks Don't Hit This

The crash is in the **"Lifted Input" stack** — code that Microsoft extracted from the OS into a separately-shipped DLL (`Microsoft.InputStateManager.dll`) so it could be updated via NuGet independently. This lifted copy is **ONLY** used by WinUI 3 / Windows App SDK apps.

- **Win32 apps** get touch input via `WM_POINTER` messages directly from the kernel
- **WPF apps** use their own input dispatcher built on `HwndSource` + `WM_POINTER`
- **UWP apps** use the system-level (non-lifted) `CoreInput` stack baked into Windows
- **Windows Shell / Explorer** is pure Win32

The lifted copy is less mature and less tested than the OS-integrated version.

---

## Root Cause Analysis

**DLL:** `Microsoft.InputStateManager.dll` v10.0.26107.1015  
**Source:** `onecoreuap\windows\moderncore\inputv2\systeminputhosts\lifted\lib\inprocinputhandler.cpp`  
**Function:** `InProcInputHandler::PopulateContactInFrame`  
**PDB (public symbols):** Downloaded from Microsoft Symbol Server (GUID `{CEF0AF5F-4DF1-BDF4-0585-2A6D9E633B3A}`)

### Summary

`PopulateContactInFrame` calls several Win32 pointer/touch input APIs and uses the WIL macro `FAIL_FAST_IF_WIN32_BOOL_FALSE()` to check their return values. These APIs can **transiently fail** under concurrent multi-touch input (due to race conditions with the window system), but the code treats every failure as a fatal, unrecoverable error and terminates the process via `__fastfail(FAST_FAIL_FATAL_APP_EXIT)`.

**Ironically**, another API call in the same function — `GetPointerInputTransform` — is handled gracefully when it fails (it sets a flag and continues). The other API calls should receive the same treatment.

### Detailed Disassembly Findings

Seven `FailFast` sites were identified inside `PopulateContactInFrame`. Three of them directly correspond to Win32 API calls that can legitimately fail under load:

#### 1. `GetPointerDeviceProperties` — Lines 621 & 627

```
+0x498:  call [_imp_GetPointerDeviceProperties]  ; 1st call
+0x4AB:  test eax, eax
+0x4AD:  je   → _FailFast_GetLastError(line 621)

+0x512:  call [_imp_GetPointerDeviceProperties]  ; 2nd call  
+0x525:  test eax, eax
+0x527:  je   → _FailFast_GetLastError(line 627)
```

The WIL macro expansion is equivalent to:
```cpp
// Current code (crashes):
FAIL_FAST_IF_WIN32_BOOL_FALSE(GetPointerDeviceProperties(device, &propertyCount, properties));
```

#### 2. `GetRawPointerDeviceData` — Line 635 ← **PRIMARY CRASH SITE**

```
+0x585:  call [_imp_GetRawPointerDeviceData]
+0x598:  test eax, eax
+0x59A:  je   → _FailFast_GetLastError(line 635)
```

This is the **exact** crash address reported in dump files (`PopulateContactInFrame+0x5a4` falls at the `int 3` dead-code marker after the noreturn FailFast call at `+0x59F`).

Equivalent source:
```cpp
// Current code (crashes):
FAIL_FAST_IF_WIN32_BOOL_FALSE(GetRawPointerDeviceData(pointerId, historyCount, propertyCount, pProperties, pValues));
```

#### 3. `ClientToScreen` — Line 677

```
+0x643:  call [_imp_ClientToScreen]
+0x656:  test eax, eax
+0x658:  je   → _FailFast_GetLastError(line 677)
```

#### 4. `GetPointerInputTransform` — ✅ HANDLED GRACEFULLY (not a crash site)

```
+0x615:  call [_imp_GetPointerInputTransform]
+0x621:  test eax, eax
+0x623:  je   → (set flag, continue processing)  ← NO FailFast!
```

This proves the development team **already knows** these APIs can fail — they just didn't apply the same treatment consistently.

### Win32 Errors Observed at Crash Time

From WinDbg analysis of crash dumps:

| Error | Value | Meaning |
|-------|-------|---------|
| `GetLastError()` | `0x12A` (298) | `ERROR_TOO_MANY_POSTS` — semaphore overloaded by rapid touch events |
| HRESULT | `0x80070578` | `ERROR_INVALID_WINDOW_HANDLE` — window handle invalidated during frame processing |

Both are **transient** errors caused by timing/concurrency, not by corrupted state.

### The Fix (for Microsoft)

Replace `FAIL_FAST_IF_WIN32_BOOL_FALSE` with graceful error handling for the three API calls that can transiently fail. At minimum:

```cpp
// INSTEAD OF:
FAIL_FAST_IF_WIN32_BOOL_FALSE(GetRawPointerDeviceData(pointerId, historyCount, propertyCount, pProperties, pValues));

// DO:
if (!GetRawPointerDeviceData(pointerId, historyCount, propertyCount, pProperties, pValues))
{
    LOG_LAST_ERROR();  // or LOG_IF_WIN32_BOOL_FALSE for telemetry
    return;            // skip this frame — don't kill the process
}
```

Apply the same pattern to `GetPointerDeviceProperties` (lines 621, 627) and `ClientToScreen` (line 677).

The `GetPointerInputTransform` call already demonstrates the correct approach — on failure it sets a fallback flag and continues. The same pattern should be applied to the other API calls.

### Why This Is Safe

- Dropping a single touch frame is imperceptible to the user (touch events arrive at 60–240 Hz).
- The Win32 pointer APIs are documented as potentially failing when the pointer ID is no longer valid (contact lifted mid-query) or when the window handle becomes stale.
- The errors `ERROR_TOO_MANY_POSTS` and `ERROR_INVALID_WINDOW_HANDLE` are explicitly **non-fatal** and **non-corrupting** — they simply mean "this particular query didn't work right now."

---

## Workaround: `InputThrottleHelper.cs`

Since the crash is caused by rapid pointer messages overwhelming the lifted input stack's semaphore, this workaround implements a **Win32 window subclass** that intercepts `WM_POINTER` messages **BEFORE** they reach WinUI 3.

### How It Works

- Uses `SetWindowSubclass` (comctl32.dll) to hook the window's HWND
- Intercepts `WM_POINTERDOWN`, `WM_POINTERUPDATE`, `WM_POINTERUP` messages
- **Rate-limits** `WM_POINTERUPDATE`: drops updates arriving within 8ms of each other per pointer ID (~125 Hz cap)
- **Limits concurrent touch contacts** to 1 (configurable) — any additional simultaneous fingers are silently dropped
- Always passes through admitted DOWN and UP events so XAML state stays clean

### Child Window Subclassing (New — March 2026)

WinUI 3 may route touch input through **internal child HWNDs** (InputSite, DesktopChildSiteBridge) rather than the top-level window. On some OS builds (e.g., Windows 10.0.26200.0), `WM_POINTER` messages never reach the top-level HWND at all — they're routed entirely through the COM pipeline via child windows.

`InstallWithChildren()` enumerates all child windows using `EnumChildWindows` and subclasses each one:

```csharp
// After the window has fully loaded (so WinUI has created its internal child HWNDs):
InputThrottleHelper.InstallWithChildren(hwnd);
```

This ensures the throttle covers all possible message paths, regardless of how the specific OS build routes touch input.

### Integration

```csharp
// After obtaining the HWND (e.g., in MainWindow constructor or Activated handler):
InputThrottleHelper.Install(hwnd);

// Or, to also subclass internal WinUI child windows (recommended):
// Defer to after WinUI creates its child HWNDs
DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
{
    InputThrottleHelper.InstallWithChildren(hwnd);
});

// In the window's Closed handler:
InputThrottleHelper.Uninstall(hwnd);
```

### Tunable Constants

| Constant | Default | Description |
|----------|---------|-------------|
| `MinUpdateIntervalMs` | 8 | Minimum ms between `WM_POINTERUPDATE` per pointer ID. Higher = more aggressive throttle. |
| `MaxConcurrentPointers` | 1 | Max simultaneous touch contacts forwarded to WinUI 3. Set to 0 for unlimited. Increase if multi-touch is needed. |

This directly targets the root cause: `GetRawPointerDeviceData` failing with `ERROR_TOO_MANY_POSTS` when the semaphore is overloaded by rapid multi-touch. By throttling at the Win32 message level, `PopulateContactInFrame` never gets called fast enough to trigger the race condition.

---

## Additional Finding: Message Pipeline Slowdown Effect

### The Paradox

On some machines (tested on Windows 10.0.26200.0), `WM_POINTER` messages don't flow through **any** HWND — not even child windows. The throttle's `WndProc` reports `pointer_msgs=0`. Yet crash frequency dropped by approximately **95%** after installing the subclass.

### Why the Subclass Helps Even Without Filtering Messages

`SetWindowSubclass` inserts a callback into the message chain for **ALL** Win32 messages, not just `WM_POINTER`. Even when no pointer messages are intercepted, the subclass provides:

1. **Message pipeline slowdown** — Every Win32 message dispatched to the subclassed HWND passes through our `WndProc` callback and its `switch(uMsg)` evaluation. This adds microseconds of overhead per message, subtly slowing the overall message pump. This gives `InputStateManager`'s internal pipeline more breathing room in its race condition.

2. **Compounding across child windows** — With 4–5+ HWNDs subclassed (main window + WinUI's internal InputSite/DesktopChildSiteBridge windows), the slowdown effect compounds across all windows, including the exact internal windows where WinUI processes touch.

3. **Dual protection on some machines** — On machines where `WM_POINTER` **does** flow through HWNDs, the throttle provides both the intended message filtering AND the pipeline slowdown — a double layer of protection.

### Implications for Implementors

- **Always use `InstallWithChildren()`** — subclassing more HWNDs increases the pipeline slowdown effect, even if `pointer_msgs` stays at 0.
- The workaround is effective across different OS builds and hardware configurations, regardless of how WinUI internally routes touch input.
- This is a strong indication that the root cause is fundamentally a **timing/race condition** — even tiny delays in message processing are enough to prevent the crash.

---

## References

- [microsoft-ui-xaml#10929](https://github.com/microsoft/microsoft-ui-xaml/issues/10929) — Original bug report
- [microsoft-ui-xaml#10674](https://github.com/microsoft/microsoft-ui-xaml/issues/10674) — Related multi-touch crash

---

*Analysis performed by disassembling `Microsoft.InputStateManager.dll` (v10.0.26107.1015, x64) using dumpbin and correlating with public symbols from the Microsoft Symbol Server.*
