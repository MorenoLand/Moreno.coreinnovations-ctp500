// Command dll builds ThermalPrint.dll (go build -buildmode=c-shared) exposing the printer to any
// language that can call a C ABI. All functions return 0 on success and a negative value on error;
// TP_LastError copies the last error message.
package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"fmt"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"os"
	"runtime"
	"strings"
	"sync"
	"time"
	"unsafe"

	_ "golang.org/x/image/bmp"

	"ctp500driver/thermal"
)

var (
	mu      sync.Mutex
	link    = &thermal.Link{}
	lastErr string
)

// All Bluetooth work runs on one dedicated OS thread. A host's calling thread may already be in
// a COM apartment mode the WinRT Bluetooth stack can't use (e.g. PowerShell/.NET STA).
var jobs = func() chan func() {
	ch := make(chan func())
	go func() {
		runtime.LockOSThread()
		for f := range ch {
			f()
		}
	}()
	return ch
}()

func onWorker(f func() C.int) C.int {
	res := make(chan C.int)
	jobs <- func() { res <- f() }
	return <-res
}

func fail(err error) C.int {
	mu.Lock()
	lastErr = err.Error()
	mu.Unlock()
	return -1
}

// TP_LastError copies the last error message (NUL-terminated) into buf and returns its length.
//
//export TP_LastError
func TP_LastError(buf *C.char, n C.int) C.int {
	mu.Lock()
	defer mu.Unlock()
	return copyOut(lastErr, buf, n)
}

func copyOut(s string, buf *C.char, n C.int) C.int {
	if n <= 0 {
		return 0
	}
	b := unsafe.Slice((*byte)(unsafe.Pointer(buf)), int(n))
	c := copy(b[:n-1], s)
	b[c] = 0
	return C.int(c)
}

// TP_Scan scans for the given seconds and writes "id<TAB>name<TAB>rssi\n" lines into buf.
//
//export TP_Scan
func TP_Scan(seconds C.int, buf *C.char, n C.int) C.int {
	return onWorker(func() C.int { return scan(seconds, buf, n) })
}

func scan(seconds C.int, buf *C.char, n C.int) C.int {
	seen := map[string]thermal.Found{}
	err := thermal.Scan(context.Background(), time.Duration(seconds)*time.Second, nil, func(f thermal.Found) {
		if old, ok := seen[f.ID]; ok && f.Name == "" {
			f.Name = old.Name
		}
		seen[f.ID] = f
	})
	if err != nil {
		return fail(err)
	}
	var sb strings.Builder
	for _, f := range seen {
		fmt.Fprintf(&sb, "%s\t%s\t%d\n", f.ID, f.Name, f.RSSI)
	}
	return copyOut(sb.String(), buf, n)
}

//export TP_Connect
func TP_Connect(id *C.char) C.int {
	addr := C.GoString(id)
	return onWorker(func() C.int {
		if err := link.Connect(addr); err != nil {
			return fail(err)
		}
		return 0
	})
}

//export TP_Disconnect
func TP_Disconnect() { onWorker(func() C.int { link.Disconnect(); return 0 }) }

//export TP_Connected
func TP_Connected() C.int {
	if link.Connected() {
		return 1
	}
	return 0
}

// TP_Status returns a bitmask: 1 paper out, 2 cover open, 4 error, 8 paper low; negative on failure.
//
//export TP_Status
func TP_Status() C.int { return onWorker(status) }

func status() C.int {
	st, err := link.QueryStatus()
	if err != nil {
		return fail(err)
	}
	var m C.int
	if st.PaperOut {
		m |= 1
	}
	if st.CoverOpen {
		m |= 2
	}
	if st.Error {
		m |= 4
	}
	if st.PaperLow {
		m |= 8
	}
	return m
}

//export TP_PrintText
func TP_PrintText(text *C.char, size C.int) C.int {
	doc := &thermal.Doc{Texts: []thermal.TextLayer{{Text: C.GoString(text), Size: int(size)}}}
	return onWorker(func() C.int {
		if err := link.Print(doc, 128, true, nil); err != nil {
			return fail(err)
		}
		return 0
	})
}

//export TP_PrintImageFile
func TP_PrintImageFile(path *C.char, threshold C.int, dither C.int) C.int {
	f, err := os.Open(C.GoString(path))
	if err != nil {
		return fail(err)
	}
	defer f.Close()
	img, _, err := image.Decode(f)
	if err != nil {
		return fail(err)
	}
	return onWorker(func() C.int {
		if err := link.Print(&thermal.Doc{Source: img}, int(threshold), dither != 0, nil); err != nil {
			return fail(err)
		}
		return 0
	})
}

// TP_PrintBitmap prints pre-packed 1-bit rows (MSB first, 1 = burn); bytesPerRow must be 48.
//
//export TP_PrintBitmap
func TP_PrintBitmap(bits *C.uchar, bytesPerRow C.int, height C.int) C.int {
	if int(bytesPerRow) != thermal.PaperW/8 {
		return fail(fmt.Errorf("bytesPerRow must be %d", thermal.PaperW/8))
	}
	data := C.GoBytes(unsafe.Pointer(bits), bytesPerRow*height)
	return onWorker(func() C.int {
		if err := link.Send(thermal.PrintModeImg()); err != nil {
			return fail(err)
		}
		time.Sleep(50 * time.Millisecond)
		if err := link.PrintBitmap(data, int(bytesPerRow), int(height), nil); err != nil {
			return fail(err)
		}
		return 0
	})
}

func main() {}
