//go:build windows

package main

import (
	"runtime"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	user32   = windows.NewLazySystemDLL("user32.dll")
	shell32  = windows.NewLazySystemDLL("shell32.dll")
	comdlg32 = windows.NewLazySystemDLL("comdlg32.dll")
	kernel32 = windows.NewLazySystemDLL("kernel32.dll")

	pSetWindowLongPtr = user32.NewProc("SetWindowLongPtrW")
	pCallWindowProc   = user32.NewProc("CallWindowProcW")
	pShowWindow       = user32.NewProc("ShowWindow")
	pSetForeground    = user32.NewProc("SetForegroundWindow")
	pPostMessage      = user32.NewProc("PostMessageW")
	pCreatePopupMenu  = user32.NewProc("CreatePopupMenu")
	pAppendMenu       = user32.NewProc("AppendMenuW")
	pTrackPopupMenu   = user32.NewProc("TrackPopupMenu")
	pDestroyMenu      = user32.NewProc("DestroyMenu")
	pGetCursorPos     = user32.NewProc("GetCursorPos")
	pLoadImage        = user32.NewProc("LoadImageW")
	pLoadIcon         = user32.NewProc("LoadIconW")
	pIsWindowVisible  = user32.NewProc("IsWindowVisible")

	pNotifyIcon     = shell32.NewProc("Shell_NotifyIconW")
	pDragAcceptFile = shell32.NewProc("DragAcceptFiles")
	pDragQueryFile  = shell32.NewProc("DragQueryFileW")
	pDragFinish     = shell32.NewProc("DragFinish")

	pGetOpenFileName = comdlg32.NewProc("GetOpenFileNameW")
	pGetModuleHandle = kernel32.NewProc("GetModuleHandleW")
)

const (
	wmClose     = 0x0010
	wmDropFiles = 0x0233
	wmTray      = 0x8001
	wmLButtonUp = 0x0202
	wmRButtonUp = 0x0205
	swHide      = 0
	swShow      = 5
	swRestore   = 9
	gwlpWndProc = ^uintptr(3) // -4
	menuShow    = 1001
	menuExit    = 1002
)

type notifyIconData struct {
	cbSize           uint32
	hWnd             uintptr
	uID              uint32
	uFlags           uint32
	uCallbackMessage uint32
	hIcon            uintptr
	szTip            [128]uint16
	dwState          uint32
	dwStateMask      uint32
	szInfo           [256]uint16
	uVersion         uint32
	szInfoTitle      [64]uint16
	dwInfoFlags      uint32
	guidItem         windows.GUID
	hBalloonIcon     uintptr
}

type point struct{ x, y int32 }

type openFileName struct {
	lStructSize       uint32
	hwndOwner         uintptr
	hInstance         uintptr
	lpstrFilter       *uint16
	lpstrCustomFilter *uint16
	nMaxCustFilter    uint32
	nFilterIndex      uint32
	lpstrFile         *uint16
	nMaxFile          uint32
	lpstrFileTitle    *uint16
	nMaxFileTitle     uint32
	lpstrInitialDir   *uint16
	lpstrTitle        *uint16
	flags             uint32
	nFileOffset       uint16
	nFileExtension    uint16
	lpstrDefExt       *uint16
	lCustData         uintptr
	lpfnHook          uintptr
	lpTemplateName    *uint16
	pvReserved        uintptr
	dwReserved        uint32
	flagsEx           uint32
}

// nativeShell wires the Gio window to the tray icon, file drops and close-to-tray.
type nativeShell struct {
	hwnd     uintptr
	orig     uintptr
	nid      notifyIconData
	quitting bool
	onDrop   func(path string)
	onWake   func()
	proc     uintptr
}

var shell *nativeShell

func installNative(hwnd uintptr, onDrop func(string), onWake func()) {
	if shell != nil && shell.hwnd == hwnd {
		return
	}
	s := &nativeShell{hwnd: hwnd, onDrop: onDrop, onWake: onWake}
	shell = s
	s.proc = syscall.NewCallback(s.wndProc)
	s.orig, _, _ = pSetWindowLongPtr.Call(hwnd, gwlpWndProc, s.proc)
	pDragAcceptFile.Call(hwnd, 1)

	hInst, _, _ := pGetModuleHandle.Call(0)
	icon, _, _ := pLoadImage.Call(hInst, 1, 1 /*IMAGE_ICON*/, 16, 16, 0)
	if icon == 0 {
		icon, _, _ = pLoadIcon.Call(0, 32512)
	}
	s.nid = notifyIconData{hWnd: hwnd, uID: 1, uFlags: 1 | 2 | 4, uCallbackMessage: wmTray, hIcon: icon}
	s.nid.cbSize = uint32(unsafe.Sizeof(s.nid))
	copy(s.nid.szTip[:], windows.StringToUTF16("ThermalPrint"))
	pNotifyIcon.Call(0, uintptr(unsafe.Pointer(&s.nid)))
}

func (s *nativeShell) wndProc(hwnd, msg, wp, lp uintptr) uintptr {
	switch msg {
	case wmClose:
		if !s.quitting {
			pShowWindow.Call(hwnd, swHide)
			return 0
		}
	case wmDropFiles:
		buf := make([]uint16, 1024)
		n, _, _ := pDragQueryFile.Call(wp, 0, uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
		pDragFinish.Call(wp)
		if n > 0 && s.onDrop != nil {
			s.onDrop(windows.UTF16ToString(buf[:n]))
		}
		return 0
	case wmTray:
		switch lp & 0xFFFF {
		case wmLButtonUp:
			toggleWindow()
		case wmRButtonUp:
			s.menu()
		}
		return 0
	}
	r, _, _ := pCallWindowProc.Call(s.orig, hwnd, msg, wp, lp)
	return r
}

func (s *nativeShell) menu() {
	m, _, _ := pCreatePopupMenu.Call()
	pAppendMenu.Call(m, 0, menuShow, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Show / Hide"))))
	pAppendMenu.Call(m, 0x800, 0, 0)
	pAppendMenu.Call(m, 0, menuExit, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Exit"))))
	var pt point
	pGetCursorPos.Call(uintptr(unsafe.Pointer(&pt)))
	pSetForeground.Call(s.hwnd)
	cmd, _, _ := pTrackPopupMenu.Call(m, 0x0002|0x0100, uintptr(pt.x), uintptr(pt.y), 0, s.hwnd, 0)
	pDestroyMenu.Call(m)
	switch cmd {
	case menuShow:
		toggleWindow()
	case menuExit:
		quitApp()
	}
}

func toggleWindow() {
	if shell == nil {
		return
	}
	vis, _, _ := pIsWindowVisible.Call(shell.hwnd)
	if vis != 0 {
		pShowWindow.Call(shell.hwnd, swHide)
		return
	}
	pShowWindow.Call(shell.hwnd, swShow)
	pShowWindow.Call(shell.hwnd, swRestore)
	pSetForeground.Call(shell.hwnd)
	if shell.onWake != nil {
		shell.onWake()
	}
}

func hideToTray() {
	if shell != nil {
		pShowWindow.Call(shell.hwnd, swHide)
	}
}

func quitApp() {
	if shell == nil {
		return
	}
	shell.quitting = true
	pNotifyIcon.Call(2, uintptr(unsafe.Pointer(&shell.nid)))
	pPostMessage.Call(shell.hwnd, wmClose, 0, 0)
}

// pickImageFile shows the native Open dialog on its own thread and returns the chosen path ("" if cancelled).
func pickImageFile() string {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	file := make([]uint16, 1024)
	filter := windows.StringToUTF16("Images\x00*.jpg;*.jpeg;*.png;*.bmp;*.gif\x00All files\x00*.*\x00")
	ofn := openFileName{
		lpstrFilter: &filter[0],
		lpstrFile:   &file[0],
		nMaxFile:    uint32(len(file)),
		lpstrTitle:  windows.StringToUTF16Ptr("Load Image"),
		flags:       0x00080000 | 0x00001000 | 0x00000800 | 0x00000004, // EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST | HIDEREADONLY
	}
	ofn.lStructSize = uint32(unsafe.Sizeof(ofn))
	if r, _, _ := pGetOpenFileName.Call(uintptr(unsafe.Pointer(&ofn))); r == 0 {
		return ""
	}
	return windows.UTF16ToString(file)
}
