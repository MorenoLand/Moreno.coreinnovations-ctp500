package thermal

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"

	"tinygo.org/x/bluetooth"
)

// WinRT GattCharacteristicProperties bits.
const (
	propRead     = 0x02
	propWriteNR  = 0x04
	propWrite    = 0x08
	propNotify   = 0x10
	propIndicate = 0x20
)

// MTU is the write chunk size used by the original app (Windows splits long writes).
const MTU = 182

// Found is one device seen during a scan.
type Found struct {
	ID   string // MAC address, e.g. "AA:BB:CC:DD:EE:FF"
	Name string
	RSSI int
}

var adapter = bluetooth.DefaultAdapter

// The WinRT Bluetooth stack must run in a multithreaded COM apartment. The GUI/host thread may
// already be initialised differently, so every BLE call runs on a dedicated, locked OS thread.
type worker struct{ ch chan func() }

func newWorker() *worker {
	w := &worker{ch: make(chan func())}
	go func() {
		runtime.LockOSThread()
		_ = adapter.Enable()
		for f := range w.ch {
			f()
		}
	}()
	return w
}

func (w *worker) do(f func() error) error {
	res := make(chan error, 1)
	w.ch <- func() { res <- f() }
	return <-res
}

var scanW, linkW = newWorker(), newWorker()

var guidID = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-([0-9a-fA-F]{12})$`)

// normalizeID accepts a MAC ("AA:BB:..") or the GUID form older builds stored
// ("00000000-0000-0000-0000-AABBCCDDEEFF") and returns the MAC form.
func normalizeID(id string) string {
	if m := guidID.FindStringSubmatch(id); m != nil {
		h := strings.ToUpper(m[1])
		return h[0:2] + ":" + h[2:4] + ":" + h[4:6] + ":" + h[6:8] + ":" + h[8:10] + ":" + h[10:12]
	}
	return id
}

// Scan reports devices until ctx is done or the timeout elapses. Names may
// arrive in a later packet than the address, so onFound can fire repeatedly per device.
func Scan(ctx context.Context, timeout time.Duration, log func(string), onFound func(Found)) error {
	done := make(chan error, 1)
	go func() {
		done <- scanW.do(func() error {
			return adapter.Scan(func(_ *bluetooth.Adapter, r bluetooth.ScanResult) {
				onFound(Found{ID: r.Address.String(), Name: r.LocalName(), RSSI: int(r.RSSI)})
			})
		})
	}()
	select {
	case <-ctx.Done():
	case <-time.After(timeout):
	case err := <-done:
		return err
	}
	_ = adapter.StopScan()
	return <-done
}

// Link is a connection to the printer.
type Link struct {
	Log    func(string)
	OnRx   func([]byte)
	OnLost func()

	reqMu     sync.Mutex
	mu        sync.Mutex
	dev       bluetooth.Device
	tx        bluetooth.DeviceCharacteristic
	connected bool
	rxCh      chan []byte
	noResp    bool
	chunk     int
}

func (l *Link) logf(f string, a ...any) {
	if l.Log != nil {
		l.Log(fmt.Sprintf(f, a...))
	}
}

func (l *Link) Connected() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.connected
}

func (l *Link) markLost() {
	l.mu.Lock()
	was := l.connected
	l.connected = false
	l.mu.Unlock()
	if was {
		l.logf("Connection lost.")
		if l.OnLost != nil {
			l.OnLost()
		}
	}
}

// Connect opens the GATT connection, finds the UART characteristics, enables
// notifications and sends ESC @ (same flow as the C# app).
func (l *Link) Connect(id string) error {
	return linkW.do(func() error { return l.connect(id) })
}

func (l *Link) connect(id string) error {
	id = normalizeID(id)
	mac, err := bluetooth.ParseMAC(id)
	if err != nil {
		return fmt.Errorf("bad device id %q", id)
	}
	l.logf("Connecting...")
	l.rxCh = make(chan []byte, 16)
	adapter.SetConnectHandler(func(_ bluetooth.Device, connected bool) {
		if !connected {
			l.markLost()
		}
	})
	dev, err := adapter.Connect(bluetooth.Address{MACAddress: bluetooth.MACAddress{MAC: mac}}, bluetooth.ConnectionParams{})
	if err != nil {
		return fmt.Errorf("connect: %w", err)
	}
	l.logf("Connected, discovering services...")
	time.Sleep(500 * time.Millisecond)

	svcs, err := dev.DiscoverServices(nil)
	if err != nil || len(svcs) == 0 {
		_ = dev.Disconnect()
		return errors.New("no services found")
	}
	var ids []string
	for _, s := range svcs {
		ids = append(ids, s.UUID().String())
	}
	l.logf("Services: %s", strings.Join(ids, ", "))
	svc := svcs[0]
	for _, s := range svcs {
		if strings.EqualFold(s.UUID().String(), ServiceUUID) {
			svc = s
		}
	}
	l.logf("Using service: %s", svc.UUID())
	chars, err := svc.DiscoverCharacteristics(nil)
	if err != nil || len(chars) == 0 {
		_ = dev.Disconnect()
		return errors.New("no characteristics found")
	}
	var tx, rx *bluetooth.DeviceCharacteristic
	for i := range chars {
		c := &chars[i]
		p := c.Properties()
		l.logf("  CHAR: %s W=%v WNR=%v R=%v N=%v", c.UUID(), p&propWrite != 0, p&propWriteNR != 0, p&propRead != 0, p&(propNotify|propIndicate) != 0)
		if strings.EqualFold(c.UUID().String(), TxUUID) {
			tx = c
		}
		if strings.EqualFold(c.UUID().String(), RxUUID) {
			rx = c
		}
	}
	for i := range chars {
		c := &chars[i]
		if tx == nil && c.Properties()&(propWrite|propWriteNR) != 0 {
			tx = c
		}
		if rx == nil && c.Properties()&(propNotify|propIndicate) != 0 {
			rx = c
		}
	}
	if tx == nil {
		_ = dev.Disconnect()
		return errors.New("no writable characteristic found")
	}
	mtu, merr := tx.GetMTU()
	if merr == nil {
		l.logf("MTU negotiated: %d", mtu)
	}
	chunk, noResp := MTU, false
	if tx.Properties()&propWrite == 0 { // only write-without-response: must fit one ATT packet
		noResp = true
		chunk = 20
		if merr == nil && mtu > 23 {
			chunk = int(mtu) - 3
		}
	}
	if rx != nil {
		err := rx.EnableNotifications(func(b []byte) {
			cp := append([]byte(nil), b...)
			l.logf("%s", DescribeResponse(cp))
			select {
			case l.rxCh <- cp:
			default:
			}
			if l.OnRx != nil {
				l.OnRx(cp)
			}
		})
		if err != nil {
			l.logf("Notifications unavailable: %v", err)
		}
	}
	l.mu.Lock()
	l.dev, l.tx, l.noResp, l.chunk, l.connected = dev, *tx, noResp, chunk, true
	l.mu.Unlock()
	if err := l.rawWrite(EscInit()); err != nil {
		return err
	}
	l.logf("Connected. Printer initialized.")
	return nil
}

// Disconnect closes the connection.
func (l *Link) Disconnect() {
	l.mu.Lock()
	was := l.connected
	l.connected = false
	dev := l.dev
	l.mu.Unlock()
	if was {
		_ = linkW.do(func() error { return dev.Disconnect() })
	}
}

func (l *Link) write(b []byte) error { return linkW.do(func() error { return l.rawWrite(b) }) }

func (l *Link) rawWrite(b []byte) error {
	l.mu.Lock()
	tx, nr, ok := l.tx, l.noResp, l.connected
	l.mu.Unlock()
	if !ok {
		return errors.New("not connected")
	}
	var err error
	if nr {
		_, err = tx.WriteWithoutResponse(b)
	} else {
		_, err = tx.Write(b)
	}
	if err != nil {
		return fmt.Errorf("write failed: %w", err)
	}
	return nil
}

// Send writes a short command, chunked, with a 10 ms gap between chunks when split.
func (l *Link) Send(cmd []byte) error {
	l.mu.Lock()
	chunk := l.chunk
	l.mu.Unlock()
	if chunk == 0 {
		chunk = MTU
	}
	for i := 0; i < len(cmd); i += chunk {
		if err := l.write(cmd[i:min(i+chunk, len(cmd))]); err != nil {
			return err
		}
		if len(cmd) > chunk {
			time.Sleep(10 * time.Millisecond)
		}
	}
	return nil
}

// PrintBitmap sends packed 1-bpp rows using the GS v 0 raster command, then feeds.
func (l *Link) PrintBitmap(bits []byte, bytesPerRow, height int, progress func(float64)) error {
	buf := make([]byte, 0, len(bits)+16)
	buf = append(buf, EscInit()...)
	buf = append(buf, 0x1D, 0x76, 0x30, 0x00, byte(bytesPerRow), byte(bytesPerRow>>8), byte(height), byte(height>>8))
	buf = append(buf, bits...)
	buf = append(buf, 0x1B, 0x4A, 0xA0)
	l.mu.Lock()
	chunk := l.chunk
	l.mu.Unlock()
	if chunk == 0 {
		chunk = MTU
	}
	l.logf("TX: %d bytes, %d chunks", len(buf), (len(buf)+chunk-1)/chunk)
	for i := 0; i < len(buf); i += chunk {
		if !l.Connected() {
			return errors.New("connection lost during print")
		}
		if err := l.write(buf[i:min(i+chunk, len(buf))]); err != nil {
			return err
		}
		time.Sleep(20 * time.Millisecond)
		if progress != nil {
			progress(min(1, float64(i+chunk)/float64(len(buf))))
		}
	}
	return nil
}

// Print renders-and-sends: switch to image mode, wait, send the bitmap.
func (l *Link) Print(d *Doc, threshold int, dither bool, progress func(float64)) error {
	bits, bpr, h := Pack(Render(d, threshold, dither))
	if err := l.Send(PrintModeImg()); err != nil {
		return err
	}
	time.Sleep(50 * time.Millisecond)
	if err := l.PrintBitmap(bits, bpr, h, progress); err != nil {
		return err
	}
	l.logf("Print complete — %d×%dpx", PaperW, h)
	return nil
}

// Request sends cmd and waits for the next notification (replies carry no command id,
// so requests must be serialized).
func (l *Link) Request(cmd []byte, timeout time.Duration) ([]byte, error) {
	l.reqMu.Lock()
	defer l.reqMu.Unlock()
	for {
		select {
		case <-l.rxCh:
			continue
		default:
		}
		break
	}
	if err := l.Send(cmd); err != nil {
		return nil, err
	}
	select {
	case r := <-l.rxCh:
		return r, nil
	case <-time.After(timeout):
		return nil, errors.New("no reply")
	}
}

// QueryStatus reads paper/cover/error state via ESC/POS real-time status requests.
func (l *Link) QueryStatus() (Status, error) {
	get := func(n byte) (byte, error) {
		r, err := l.Request([]byte{0x10, 0x04, n}, 800*time.Millisecond)
		if err != nil || len(r) == 0 {
			return 0, errors.New("no status reply")
		}
		return r[0], nil
	}
	off, err := get(2)
	if err != nil {
		return Status{}, err
	}
	ec, err := get(3)
	if err != nil {
		return Status{}, err
	}
	pp, err := get(4)
	if err != nil {
		return Status{}, err
	}
	return DecodeStatus(off, ec, pp), nil
}
