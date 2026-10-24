// ThermalPrint — Windows companion for the Core Innovations CTP500BR BLE thermal printer.
package main

import (
	"bytes"
	"context"
	_ "embed"
	"encoding/hex"
	"fmt"
	xdraw "golang.org/x/image/draw"
	"image"
	"image/color"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"os"
	"sort"
	"strings"
	"sync"
	"time"

	"gioui.org/app"
	"gioui.org/f32"
	"gioui.org/gesture"
	"gioui.org/io/clipboard"
	"gioui.org/io/pointer"
	"gioui.org/io/system"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/unit"
	"gioui.org/widget"
	"gioui.org/widget/material"
	_ "golang.org/x/image/bmp"

	"ctp500driver/thermal"
)

//go:embed assets/thermalprint.png
var iconPNG []byte

var densityNames = []string{"Lightest", "Light", "Default", "Dark", "Darkest"}

type device struct {
	id, name string
	rssi     int
	btn      widget.Clickable
}

type App struct {
	w  *app.Window
	th *material.Theme

	jmu sync.Mutex
	jq  []func()

	set  *thermal.Settings
	link *thermal.Link
	doc  thermal.Doc

	// status
	statusTxt  string
	statusCol  colorN
	paperTxt   string
	paperCol   colorN
	connected  bool
	busy       bool // connecting / scanning
	printing   bool
	progress   float64
	smu        sync.Mutex
	lastStatus *thermal.Status
	maximized  bool
	imgName    string

	// log
	logLines []string
	logEd    widget.Editor
	logDirty bool

	// preview cache
	prevDirty bool
	prevOp    paint.ImageOp
	prevW     int
	prevH     int
	iconOp    paint.ImageOp
	hasIcon   bool

	// widgets
	sideList, prevList                                             widget.List
	btnMin, btnMax, btnClose                                       widget.Clickable
	btnConnect, btnChoose, btnFeed, btnSelf, btnStop, btnRefresh   widget.Clickable
	btnProbe, btnRaw, btnLoad, btnAdd, btnUndo, btnClear, btnPrint widget.Clickable
	btnCopy, btnClearLog, btnUp, btnDown                           widget.Clickable
	density, thresh                                                widget.Float
	feedEd, rawEd, textEd                                          widget.Editor
	bold, dither, invert                                           widget.Bool
	size, align                                                    *dropdown
	split                                                          gesture.Drag
	splitStart                                                     float32
	consoleH                                                       int // dp

	// modal
	modal                         int // 0 none, 1 picker, 2 message
	msg                           string
	msgOK                         widget.Clickable
	devices                       map[string]*device
	pickSel                       string
	hideUnnam                     widget.Bool
	pkRescan, pkCancel, pkConnect widget.Clickable
	pkList                        widget.List
	scanning                      bool
	scanStop                      context.CancelFunc
}

type colorN = color.NRGBA

func main() {
	a := &App{
		th: newTheme(), set: thermal.LoadSettings(), consoleH: 160,
		statusTxt: "● Disconnected", prevDirty: true,
		size: newDropdown("12", "16", "20", "24", "32", "48"), align: newDropdown("Left", "Center", "Right"),
	}
	a.statusCol = cOrange
	a.size.sel, a.align.sel = 3, 0
	a.sideList.Axis = layout.Vertical
	a.prevList.Axis = layout.Vertical
	a.pkList.Axis = layout.Vertical
	a.density.Value = float32(a.set.Density-1) / 4
	a.thresh.Value = float32(a.set.Threshold) / 255
	a.dither.Value = a.set.Dither
	a.invert.Value = a.set.Invert
	a.doc.Invert = a.set.Invert
	a.hideUnnam.Value = true
	a.feedEd.SingleLine, a.feedEd.Filter = true, "0123456789"
	a.feedEd.SetText("20")
	a.rawEd.SingleLine = true
	a.textEd.SetText("Hello World")
	a.logEd.ReadOnly = true
	if src, _, err := image.Decode(bytes.NewReader(iconPNG)); err == nil {
		ic := image.NewNRGBA(image.Rect(0, 0, 40, 40))
		xdraw.CatmullRom.Scale(ic, ic.Bounds(), src, src.Bounds(), xdraw.Over, nil)
		a.iconOp, a.hasIcon = paint.NewImageOp(ic), true
	}
	a.link = &thermal.Link{Log: a.logf, OnRx: a.onRx, OnLost: func() {
		a.post(func() { a.connected = false; a.setStatus("● Connection lost", cRed); a.clearReadings() })
	}}

	go func() {
		w := new(app.Window)
		a.w = w
		w.Option(app.Title("ThermalPrint"), app.Size(960, 780), app.MinSize(720, 560), app.Decorated(false))
		if err := a.run(w); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		os.Exit(0)
	}()
	app.Main()
}

func (a *App) post(f func()) {
	a.jmu.Lock()
	a.jq = append(a.jq, f)
	a.jmu.Unlock()
	if a.w != nil {
		a.w.Invalidate()
	}
}

func (a *App) drain() {
	a.jmu.Lock()
	q := a.jq
	a.jq = nil
	a.jmu.Unlock()
	for _, f := range q {
		f()
	}
}

func (a *App) logf(s string) {
	line := fmt.Sprintf("[%s] %s", time.Now().Format("15:04:05.000"), s)
	a.post(func() {
		a.logLines = append(a.logLines, line)
		if len(a.logLines) > 500 {
			a.logLines = a.logLines[len(a.logLines)-500:]
		}
		a.logDirty = true
	})
}

func (a *App) setStatus(t string, c colorN) { a.statusTxt, a.statusCol = t, c }

func (a *App) clearReadings() {
	a.paperTxt = ""
	a.smu.Lock()
	a.lastStatus = nil
	a.smu.Unlock()
}

func (a *App) showMsg(m string) { a.post(func() { a.msg, a.modal = m, 2 }) }

func (a *App) run(w *app.Window) error {
	var ops op.Ops
	for {
		switch e := w.Event().(type) {
		case app.DestroyEvent:
			return e.Err
		case app.Win32ViewEvent:
			if e.HWND != 0 {
				go installNative(e.HWND, a.loadPath, func() { w.Invalidate() })
			}
		case app.FrameEvent:
			gtx := app.NewContext(&ops, e)
			a.drain()
			a.layout(gtx)
			e.Frame(gtx.Ops)
		}
	}
}

// ---- flows ----

func (a *App) onRx(d []byte) {}

func (a *App) applyStatus(st thermal.Status) {
	a.smu.Lock()
	a.lastStatus = &st
	a.smu.Unlock()
	switch {
	case st.PaperOut:
		a.paperTxt, a.paperCol = "⚠ Paper out", cOrange
	case st.CoverOpen:
		a.paperTxt, a.paperCol = "⚠ Cover open", cOrange
	case st.Error:
		a.paperTxt, a.paperCol = "⚠ Printer error (overheat?)", cRed
	case st.PaperLow:
		a.paperTxt, a.paperCol = "⚠ Paper low", cOrange
	default:
		a.paperTxt, a.paperCol = "● Paper OK", cGreen
	}
}

func (a *App) connectTo(id string) error {
	a.post(func() { a.busy = true; a.setStatus("● Connecting...", cOrange) })
	defer a.post(func() { a.busy = false })
	if err := a.link.Connect(id); err != nil {
		a.link.Disconnect()
		return err
	}
	a.set.LastDevice = &id
	a.set.Save()
	a.post(func() { a.connected = true; a.setStatus("● Connected", cGreen) })
	time.Sleep(200 * time.Millisecond)
	_ = a.link.Send(thermal.SetDensity(a.set.Density - 1))
	a.logf(fmt.Sprintf("Density → level %d", a.set.Density))
	a.refreshStatus()
	return nil
}

func (a *App) connectFlow(forcePick bool) {
	if a.link.Connected() {
		a.link.Disconnect()
		a.connected = false
		a.setStatus("● Disconnected", cOrange)
		a.clearReadings()
		return
	}
	go func() {
		if !forcePick && a.set.LastDevice != nil {
			if err := a.connectTo(*a.set.LastDevice); err == nil {
				return
			} else {
				a.logf("Reconnect failed (" + err.Error() + "); choose a device.")
			}
		}
		a.post(a.openPicker)
	}()
}

func (a *App) openPicker() {
	a.modal = 1
	a.pickSel = ""
	a.devices = map[string]*device{}
	a.startScan()
}

func (a *App) startScan() {
	if a.scanStop != nil {
		a.scanStop()
	}
	ctx, cancel := context.WithCancel(context.Background())
	a.scanStop = cancel
	a.scanning = true
	go func() {
		err := thermal.Scan(ctx, 10*time.Second, a.logf, func(f thermal.Found) {
			a.post(func() {
				d := a.devices[f.ID]
				if d == nil {
					d = &device{id: f.ID}
					a.devices[f.ID] = d
				}
				if f.Name != "" {
					d.name = f.Name
				}
				d.rssi = f.RSSI
			})
		})
		if err != nil {
			a.logf("Scan error: " + err.Error())
		}
		a.post(func() {
			if ctx.Err() == nil {
				a.scanning = false
			}
		})
	}()
}

func (a *App) closePicker() {
	if a.scanStop != nil {
		a.scanStop()
	}
	a.scanning = false
	a.modal = 0
}

func (a *App) acceptPicker() {
	id := a.pickSel
	if id == "" {
		return
	}
	a.closePicker()
	go func() {
		if err := a.connectTo(id); err != nil {
			a.post(func() { a.setStatus("● Connect failed", cRed) })
			a.logf("ERROR: " + err.Error())
		}
	}()
}

func (a *App) refreshStatus() {
	go func() {
		st, err := a.link.QueryStatus()
		if err != nil {
			a.logf("Status: " + err.Error())
			return
		}
		a.post(func() { a.applyStatus(st) })
	}()
}

func (a *App) poll() {
	for range time.Tick(30 * time.Second) {
		if a.link.Connected() && !a.printing {
			a.refreshStatus()
		}
	}
}

// probe sends read-only query frames so the console shows whether this firmware answers them.
func (a *App) probe() {
	go func() {
		qs := []struct {
			n string
			b []byte
		}{
			{"vendor GET_STATUS 51 78 A3", thermal.GetStatus()}, {"vendor GET_BATTERY 51 78 BA", thermal.GetBattery()},
			{"vendor GET_INFO 51 78 A8", thermal.GetInfo()}, {"vendor GET_DEV_ID 51 78 BB", thermal.GetDevID()},
			{"ESC/POS DLE EOT 1 (printer status)", []byte{0x10, 0x04, 0x01}},
			{"ESC/POS DLE EOT 2 (offline cause)", []byte{0x10, 0x04, 0x02}},
			{"ESC/POS DLE EOT 3 (error cause)", []byte{0x10, 0x04, 0x03}},
			{"ESC/POS DLE EOT 4 (paper sensor)", []byte{0x10, 0x04, 0x04}},
			{"ESC/POS GS I 1 (model id)", []byte{0x1D, 0x49, 0x01}},
		}
		for _, q := range qs {
			a.logf("PROBE → " + q.n)
			if err := a.link.Send(q.b); err != nil {
				a.logf("probe failed: " + err.Error())
				return
			}
			time.Sleep(600 * time.Millisecond)
		}
		a.logf("PROBE done — any RX lines above are replies from the printer.")
	}()
}

func (a *App) sendRaw() {
	h := strings.Map(func(r rune) rune {
		if r == ' ' || r == ',' || r == ':' {
			return -1
		}
		return r
	}, a.rawEd.Text())
	b, err := hex.DecodeString(h)
	if err != nil || len(b) == 0 {
		a.showMsg("Enter hex bytes, e.g. 10 04 01")
		return
	}
	a.logf("TX raw: " + strings.ToUpper(hex.EncodeToString(b)))
	go func() {
		if err := a.link.Send(b); err != nil {
			a.logf("send failed: " + err.Error())
		}
	}()
}

func (a *App) loadPath(path string) {
	go func() {
		f, err := os.Open(path)
		if err != nil {
			a.logf("Image load failed: " + err.Error())
			return
		}
		defer f.Close()
		img, _, err := image.Decode(f)
		if err != nil {
			a.logf("Unsupported image: " + path)
			a.showMsg("Could not load image: unsupported format.")
			return
		}
		name := path
		if i := strings.LastIndexAny(path, `\/`); i >= 0 {
			name = path[i+1:]
		}
		a.post(func() { a.doc.Source, a.imgName, a.prevDirty = img, name, true })
	}()
}

func (a *App) print() {
	if !a.link.Connected() {
		a.showMsg("Connect printer first")
		return
	}
	if a.doc.Source == nil && len(a.doc.Texts) == 0 {
		a.showMsg("Nothing to print — add an image or text first.")
		return
	}
	doc := a.doc
	doc.Texts = append([]thermal.TextLayer(nil), a.doc.Texts...)
	th, dither := a.set.Threshold, a.set.Dither
	a.printing, a.progress = true, 0
	go func() {
		defer a.post(func() { a.printing = false })
		// Fresh status first; only a reported fault blocks printing (no reply never blocks).
		if st, err := a.link.QueryStatus(); err == nil {
			a.post(func() { a.applyStatus(st) })
			if !st.OK() {
				why := "The printer reports an error — let it cool down and check the paper."
				if st.PaperOut {
					why = "The printer is out of paper."
				} else if st.CoverOpen {
					why = "The printer cover is open."
				}
				a.logf("Print blocked: " + why)
				a.showMsg(why)
				return
			}
		}
		err := a.link.Print(&doc, th, dither, func(p float64) { a.post(func() { a.progress = p }) })
		if err != nil {
			a.logf("Print ERROR: " + err.Error())
			a.showMsg("Print error: " + err.Error())
			return
		}
		a.post(func() { a.setStatus("● Printed", cGreen) })
	}()
}

// ---- layout ----

func (a *App) layout(gtx C) D {
	a.handleEvents(gtx)
	if a.prevDirty {
		a.rebuildPreview()
	}
	if a.logDirty {
		a.logEd.SetText(strings.Join(a.logLines, "\n"))
		n := a.logEd.Len()
		a.logEd.SetCaret(n, n)
		a.logDirty = false
	}
	if a.printing || a.busy || a.scanning {
		gtx.Execute(op.InvalidateCmd{At: gtx.Now.Add(40 * time.Millisecond)})
	}
	fill(gtx, cBG, gtx.Constraints.Max)
	main := gtx
	if a.modal != 0 {
		main = gtx.Disabled()
	}
	d := layout.Flex{Axis: layout.Vertical}.Layout(main,
		layout.Rigid(a.titleBar),
		layout.Flexed(1, a.body),
	)
	if a.modal != 0 {
		a.layoutModal(gtx)
	}
	return d
}

func (a *App) handleEvents(gtx C) {
	if a.btnMin.Clicked(gtx) {
		a.w.Perform(system.ActionMinimize)
	}
	if a.btnMax.Clicked(gtx) {
		if a.maximized {
			a.w.Perform(system.ActionUnmaximize)
		} else {
			a.w.Perform(system.ActionMaximize)
		}
		a.maximized = !a.maximized
	}
	if a.btnClose.Clicked(gtx) {
		hideToTray()
	}
	if a.btnConnect.Clicked(gtx) {
		a.connectFlow(false)
	}
	if a.btnChoose.Clicked(gtx) {
		a.connectFlow(true)
	}
	if a.btnFeed.Clicked(gtx) {
		n := 20
		fmt.Sscanf(a.feedEd.Text(), "%d", &n)
		n = max(1, min(1000, n))
		a.logf(fmt.Sprintf("Feed %d lines", n))
		go a.link.Send(thermal.FeedLines(n))
	}
	if a.btnSelf.Clicked(gtx) {
		a.logf("Self-test sent")
		go a.link.Send(thermal.SelfTest())
	}
	if a.btnStop.Clicked(gtx) {
		a.logf("Stop sent")
		go a.link.Send(thermal.StopPrint())
	}
	if a.btnRefresh.Clicked(gtx) {
		a.refreshStatus()
	}
	if a.btnProbe.Clicked(gtx) {
		a.probe()
	}
	if a.btnRaw.Clicked(gtx) {
		a.sendRaw()
	}
	if a.btnLoad.Clicked(gtx) {
		go func() {
			if p := pickImageFile(); p != "" {
				a.loadPath(p)
			}
		}()
	}
	if a.btnAdd.Clicked(gtx) && strings.TrimSpace(a.textEd.Text()) != "" {
		var sz int
		fmt.Sscanf(a.size.items[a.size.sel], "%d", &sz)
		a.doc.Texts = append(a.doc.Texts, thermal.TextLayer{Text: a.textEd.Text(), Size: sz, Align: a.align.sel, Bold: a.bold.Value})
		a.prevDirty = true
	}
	if a.btnUndo.Clicked(gtx) && len(a.doc.Texts) > 0 {
		a.doc.Texts = a.doc.Texts[:len(a.doc.Texts)-1]
		a.prevDirty = true
	}
	if a.btnClear.Clicked(gtx) {
		a.doc.Texts, a.doc.Source, a.imgName, a.prevDirty = nil, nil, "", true
	}
	if a.btnPrint.Clicked(gtx) && !a.printing {
		a.print()
	}
	if a.btnCopy.Clicked(gtx) {
		gtx.Execute(clipboard.WriteCmd{Type: "application/text", Data: io.NopCloser(strings.NewReader(strings.Join(a.logLines, "\n")))})
	}
	if a.btnClearLog.Clicked(gtx) {
		a.logLines, a.logDirty = nil, true
	}
	// sliders
	lvl := int(a.density.Value*4 + 0.5)
	if lvl+1 != a.set.Density {
		a.set.Density = lvl + 1
		a.set.Save()
		if a.link.Connected() {
			go a.link.Send(thermal.SetDensity(lvl))
			a.logf(fmt.Sprintf("Density → level %d", lvl+1))
		}
	}
	if t := int(a.thresh.Value*255 + 0.5); t != a.set.Threshold {
		a.set.Threshold = t
		a.set.Save()
		a.prevDirty = true
	}
	if a.dither.Update(gtx) {
		a.set.Dither = a.dither.Value
		a.set.Save()
		a.prevDirty = true
	}
	if a.invert.Update(gtx) {
		a.set.Invert, a.doc.Invert = a.invert.Value, a.invert.Value
		a.set.Save()
		a.prevDirty = true
	}
	if a.hideUnnam.Update(gtx) {
		a.pickSel = ""
	}
}

func (a *App) rebuildPreview() {
	g := thermal.Render(&a.doc, a.set.Threshold, a.set.Dither)
	pv := thermal.Preview(g)
	a.prevOp = paint.NewImageOp(pv)
	a.prevW, a.prevH = pv.Bounds().Dx(), pv.Bounds().Dy()
	a.prevDirty = false
}

func (a *App) titleBar(gtx C) D {
	h := gtx.Dp(32)
	sz := image.Pt(gtx.Constraints.Max.X, h)
	fill(gtx, cPanel, sz)
	// drag area
	func() {
		defer clip.Rect{Max: sz}.Push(gtx.Ops).Pop()
		system.ActionInputOp(system.ActionMove).Add(gtx.Ops)
	}()
	gtx.Constraints = layout.Exact(sz)
	layout.Flex{Alignment: layout.Middle}.Layout(gtx,
		layout.Flexed(1, func(gtx C) D {
			return layout.Center.Layout(gtx, func(gtx C) D {
				return layout.Flex{Alignment: layout.Middle}.Layout(gtx,
					layout.Rigid(func(gtx C) D {
						if !a.hasIcon {
							return D{}
						}
						sz := image.Pt(gtx.Dp(20), gtx.Dp(20))
						st := clip.Rect{Max: sz}.Push(gtx.Ops)
						tr := op.Affine(f32.Affine2D{}.Scale(f32.Point{}, f32.Pt(float32(sz.X)/40, float32(sz.Y)/40))).Push(gtx.Ops)
						a.iconOp.Add(gtx.Ops)
						paint.PaintOp{}.Add(gtx.Ops)
						tr.Pop()
						st.Pop()
						return D{Size: image.Pt(sz.X+gtx.Dp(6), sz.Y)}
					}),
					layout.Rigid(a.label("THERMAL PRINT", 12, cText, true)),
				)
			})
		}),
		layout.Rigid(func(gtx C) D { return a.winBtn(gtx, &a.btnMin, 0) }),
		layout.Rigid(func(gtx C) D { return a.winBtn(gtx, &a.btnMax, 1) }),
		layout.Rigid(func(gtx C) D { return a.winBtn(gtx, &a.btnClose, 2) }),
	)
	return D{Size: sz}
}

func (a *App) winBtn(gtx C, c *widget.Clickable, kind int) D {
	return c.Layout(gtx, func(gtx C) D {
		sz := image.Pt(gtx.Dp(46), gtx.Dp(32))
		col := cDim
		if c.Hovered() {
			if kind == 2 {
				fill(gtx, cRed, sz)
				col = cWhite
			} else {
				fill(gtx, cSurface, sz)
				col = cText
			}
		}
		cx, cy := float32(sz.X)/2, float32(sz.Y)/2
		s := float32(gtx.Dp(5))
		switch kind {
		case 0:
			strokeLine(gtx, col, 1.2, f32.Pt(cx-s, cy), f32.Pt(cx+s, cy))
		case 1:
			strokeLine(gtx, col, 1.2, f32.Pt(cx-s, cy-s), f32.Pt(cx+s, cy-s), f32.Pt(cx+s, cy+s), f32.Pt(cx-s, cy+s), f32.Pt(cx-s, cy-s))
		case 2:
			strokeLine(gtx, col, 1.2, f32.Pt(cx-s, cy-s), f32.Pt(cx+s, cy+s))
			strokeLine(gtx, col, 1.2, f32.Pt(cx+s, cy-s), f32.Pt(cx-s, cy+s))
		}
		return D{Size: sz}
	})
}

func (a *App) body(gtx C) D {
	return layout.Flex{}.Layout(gtx,
		layout.Rigid(func(gtx C) D {
			w := gtx.Dp(260)
			gtx.Constraints = layout.Exact(image.Pt(w, gtx.Constraints.Max.Y))
			fill(gtx, cPanel, gtx.Constraints.Max)
			return a.sidebar(gtx)
		}),
		layout.Flexed(1, func(gtx C) D {
			return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
				layout.Flexed(1, a.preview),
				layout.Rigid(a.splitter),
				layout.Rigid(a.console),
			)
		}),
	)
}

func (a *App) preview(gtx C) D {
	fill(gtx, cBG, gtx.Constraints.Max)
	return material.List(a.th, &a.prevList).Layout(gtx, 1, func(gtx C, _ int) D {
		return layout.Inset{Top: 16, Left: 16, Right: 16, Bottom: 16}.Layout(gtx, func(gtx C) D {
			return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
				layout.Rigid(func(gtx C) D {
					sz := image.Pt(gtx.Dp(unit.Dp(a.prevW)), gtx.Dp(unit.Dp(a.prevH)))
					// 1 image dot = 1 dp so the paper looks the same size at any DPI.
					defer clip.Rect{Max: sz}.Push(gtx.Ops).Pop()
					scale := float32(sz.X) / float32(a.prevW)
					defer op.Affine(f32.Affine2D{}.Scale(f32.Point{}, f32.Pt(scale, scale))).Push(gtx.Ops).Pop()
					a.prevOp.Add(gtx.Ops)
					paint.PaintOp{}.Add(gtx.Ops)
					return D{Size: sz}
				}),
				layout.Rigid(func(gtx C) D {
					return layout.Inset{Top: 6}.Layout(gtx, a.label(
						fmt.Sprintf("%d dots × %d rows  (48 mm × %.1f mm @ 200 DPI)", a.prevW, a.prevH, float64(a.prevH)/8), 10, cDim, false))
				}),
			)
		})
	})
}

func (a *App) splitter(gtx C) D {
	sz := image.Pt(gtx.Constraints.Max.X, gtx.Dp(6))
	fill(gtx, cBorder, sz)
	area := clip.Rect{Max: sz}.Push(gtx.Ops)
	pointer.CursorRowResize.Add(gtx.Ops)
	a.split.Add(gtx.Ops)
	area.Pop()
	for {
		ev, ok := a.split.Update(gtx.Metric, gtx.Source, gesture.Vertical)
		if !ok {
			break
		}
		switch ev.Kind {
		case pointer.Press:
			a.splitStart = ev.Position.Y
		case pointer.Drag:
			delta := ev.Position.Y - a.splitStart
			a.consoleH -= int(delta / gtx.Metric.PxPerDp)
			a.consoleH = max(60, min(500, a.consoleH))
		}
	}
	return D{Size: sz}
}

func (a *App) console(gtx C) D {
	h := gtx.Dp(unit.Dp(a.consoleH))
	sz := image.Pt(gtx.Constraints.Max.X, h)
	gtx.Constraints = layout.Exact(sz)
	fill(gtx, cPanel, sz)
	return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
		layout.Rigid(func(gtx C) D {
			return layout.Inset{Left: 8, Right: 6, Top: 3, Bottom: 3}.Layout(gtx, func(gtx C) D {
				return layout.Flex{Alignment: layout.Middle}.Layout(gtx,
					layout.Flexed(1, a.label("CONSOLE", 10, cDim, true)),
					layout.Rigid(func(gtx C) D {
						gtx.Constraints.Max.X = gtx.Dp(54)
						return a.button(gtx, &a.btnCopy, "Copy", 22, kFlat, true)
					}),
					layout.Rigid(layout.Spacer{Width: 4}.Layout),
					layout.Rigid(func(gtx C) D {
						gtx.Constraints.Max.X = gtx.Dp(54)
						return a.button(gtx, &a.btnClearLog, "Clear", 22, kFlat, true)
					}),
				)
			})
		}),
		layout.Flexed(1, func(gtx C) D {
			fill(gtx, cBG, gtx.Constraints.Max)
			return layout.UniformInset(8).Layout(gtx, func(gtx C) D {
				e := material.Editor(a.th, &a.logEd, "")
				e.Font.Typeface = "Consolas"
				e.TextSize = 11
				e.Color = cDim
				return e.Layout(gtx)
			})
		}),
	)
}

func (a *App) sidebar(gtx C) D {
	items := []layout.Widget{
		a.header("PRINTER"),
		func(gtx C) D { return a.label(a.statusTxt, 11, a.statusCol, false)(gtx) },
		func(gtx C) D {
			if a.paperTxt == "" {
				return D{}
			}
			return layout.Inset{Bottom: 4}.Layout(gtx, a.label(a.paperTxt, 10, a.paperCol, false))
		},
		func(gtx C) D {
			t := "Connect to Printer"
			if a.connected {
				t = "Disconnect"
			} else if a.set.LastDevice != nil {
				t = "Reconnect"
			}
			if a.busy {
				t = "Connecting..."
			}
			return a.button(gtx, &a.btnConnect, t, 32, kFlat, !a.busy)
		},
		func(gtx C) D {
			if a.connected {
				return D{}
			}
			return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.button(gtx, &a.btnChoose, "Choose device...", 26, kFlat, !a.busy) })
		},
	}
	if a.connected {
		items = append(items,
			func(gtx C) D {
				lvl := int(a.density.Value*4 + 0.5)
				return layout.Inset{Top: 8}.Layout(gtx, a.label(fmt.Sprintf("Density: %d (%s)", lvl+1, densityNames[lvl]), 10, cDim, false))
			},
			func(gtx C) D {
				s := material.Slider(a.th, &a.density)
				s.Color = cAccent
				return s.Layout(gtx)
			},
			func(gtx C) D {
				return layout.Inset{Top: 6}.Layout(gtx, func(gtx C) D {
					return layout.Flex{Alignment: layout.Middle}.Layout(gtx,
						layout.Flexed(1, func(gtx C) D {
							return bordered(gtx, cBG, func(gtx C) D {
								return layout.UniformInset(6).Layout(gtx, func(gtx C) D {
									e := material.Editor(a.th, &a.feedEd, "20")
									e.Color = cText
									return e.Layout(gtx)
								})
							})
						}),
						layout.Rigid(layout.Spacer{Width: 4}.Layout),
						layout.Flexed(1, func(gtx C) D { return a.button(gtx, &a.btnFeed, "Feed Lines", 30, kFlat, true) }),
					)
				})
			},
			func(gtx C) D {
				return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D {
					return layout.Flex{}.Layout(gtx,
						layout.Flexed(1, func(gtx C) D { return a.button(gtx, &a.btnSelf, "Self Test", 32, kFlat, true) }),
						layout.Flexed(1, func(gtx C) D { return a.button(gtx, &a.btnStop, "Stop", 32, kFlat, true) }),
					)
				})
			},
			func(gtx C) D {
				return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.button(gtx, &a.btnRefresh, "Refresh Status", 28, kFlat, true) })
			},
			func(gtx C) D {
				return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.button(gtx, &a.btnProbe, "Probe printer replies", 28, kFlat, true) })
			},
			func(gtx C) D {
				return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D {
					return layout.Flex{Alignment: layout.Middle}.Layout(gtx,
						layout.Flexed(1, func(gtx C) D {
							return bordered(gtx, cBG, func(gtx C) D {
								return layout.UniformInset(6).Layout(gtx, func(gtx C) D {
									e := material.Editor(a.th, &a.rawEd, "raw hex, e.g. 10 04 01")
									e.Color = cText
									e.TextSize = 11
									return e.Layout(gtx)
								})
							})
						}),
						layout.Rigid(layout.Spacer{Width: 4}.Layout),
						layout.Rigid(func(gtx C) D {
							gtx.Constraints.Max.X = gtx.Dp(50)
							return a.button(gtx, &a.btnRaw, "Send", 30, kFlat, true)
						}),
					)
				})
			},
		)
	}
	items = append(items,
		a.header("IMAGE"),
		func(gtx C) D { return a.button(gtx, &a.btnLoad, "Load Image", 32, kFlat, true) },
		func(gtx C) D {
			t := "…or drag an image onto the window"
			if a.imgName != "" {
				t = a.imgName
			}
			return layout.Inset{Top: 2}.Layout(gtx, a.label(t, 10, cDim, false))
		},
		a.header("TEXT"),
		func(gtx C) D {
			return bordered(gtx, cBG, func(gtx C) D {
				gtx.Constraints.Min.Y = gtx.Dp(60)
				return layout.UniformInset(6).Layout(gtx, func(gtx C) D {
					gtx.Constraints.Min.Y = gtx.Dp(48)
					e := material.Editor(a.th, &a.textEd, "")
					e.Color = cText
					return e.Layout(gtx)
				})
			})
		},
		func(gtx C) D { return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.size.layout(a, gtx) }) },
		func(gtx C) D {
			return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.align.layout(a, gtx) })
		},
		func(gtx C) D {
			return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D {
				c := material.CheckBox(a.th, &a.bold, "Bold")
				c.IconColor, c.Color = cAccent, cText
				return c.Layout(gtx)
			})
		},
		func(gtx C) D { return a.button(gtx, &a.btnAdd, "Add Text", 32, kFlat, true) },
		func(gtx C) D {
			return layout.Inset{Top: 4}.Layout(gtx, func(gtx C) D { return a.button(gtx, &a.btnUndo, "Remove Last Text", 32, kFlat, true) })
		},
		a.header("ADJUST"),
		func(gtx C) D { return a.label(fmt.Sprintf("Threshold %d", a.set.Threshold), 11, cDim, false)(gtx) },
		func(gtx C) D {
			s := material.Slider(a.th, &a.thresh)
			s.Color = cAccent
			return s.Layout(gtx)
		},
		func(gtx C) D {
			c := material.CheckBox(a.th, &a.dither, "Dither photos (Floyd-Steinberg)")
			c.IconColor, c.Color = cAccent, cText
			return c.Layout(gtx)
		},
		func(gtx C) D {
			c := material.CheckBox(a.th, &a.invert, "Invert output")
			c.IconColor, c.Color = cAccent, cText
			return c.Layout(gtx)
		},
		func(gtx C) D {
			sz := image.Pt(gtx.Constraints.Max.X, 1)
			fill(gtx, cBorder, sz)
			return D{Size: image.Pt(sz.X, gtx.Dp(25))}
		},
		func(gtx C) D { return a.button(gtx, &a.btnClear, "Clear All", 32, kFlat, true) },
		func(gtx C) D {
			t := "PRINT"
			if a.printing {
				t = "Printing..."
			}
			return layout.Inset{Top: 10}.Layout(gtx, func(gtx C) D { return a.button(gtx, &a.btnPrint, t, 44, kAccent, !a.printing) })
		},
		func(gtx C) D {
			if !a.printing {
				return D{}
			}
			return layout.Inset{Top: 6}.Layout(gtx, func(gtx C) D {
				p := material.ProgressBar(a.th, float32(a.progress))
				p.Color, p.TrackColor = cAccent, cSurface
				return p.Layout(gtx)
			})
		},
	)
	return layout.Inset{Left: 16, Right: 16, Top: 12, Bottom: 12}.Layout(gtx, func(gtx C) D {
		return material.List(a.th, &a.sideList).Layout(gtx, len(items), func(gtx C, i int) D { return items[i](gtx) })
	})
}

// ---- modal ----

func (a *App) layoutModal(gtx C) {
	fill(gtx, color.NRGBA{A: 150}, gtx.Constraints.Max)
	layout.Center.Layout(gtx, func(gtx C) D {
		w := gtx.Dp(420)
		gtx.Constraints.Max.X, gtx.Constraints.Min.X = w, w
		m := op.Record(gtx.Ops)
		var d D
		if a.modal == 1 {
			d = a.pickerCard(gtx)
		} else {
			d = a.msgCard(gtx)
		}
		call := m.Stop()
		fill(gtx, cPanel, d.Size)
		call.Add(gtx.Ops)
		outline(gtx, cBorder, d.Size)
		return d
	})
}

func (a *App) msgCard(gtx C) D {
	if a.msgOK.Clicked(gtx) {
		a.modal = 0
	}
	return layout.UniformInset(20).Layout(gtx, func(gtx C) D {
		return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
			layout.Rigid(a.label(a.msg, 12, cText, false)),
			layout.Rigid(layout.Spacer{Height: 16}.Layout),
			layout.Rigid(func(gtx C) D {
				return layout.E.Layout(gtx, func(gtx C) D {
					gtx.Constraints.Max.X = gtx.Dp(80)
					return a.button(gtx, &a.msgOK, "OK", 30, kAccent, true)
				})
			}),
		)
	})
}

func (a *App) pickerCard(gtx C) D {
	if a.pkCancel.Clicked(gtx) {
		a.closePicker()
	}
	if a.pkRescan.Clicked(gtx) {
		a.devices = map[string]*device{}
		a.pickSel = ""
		a.startScan()
	}
	if a.pkConnect.Clicked(gtx) {
		a.acceptPicker()
	}
	var list []*device
	for _, d := range a.devices {
		if a.hideUnnam.Value && d.name == "" {
			continue
		}
		list = append(list, d)
	}
	isPrinter := func(d *device) bool { return strings.Contains(strings.ToLower(d.name), "print") }
	sort.Slice(list, func(i, j int) bool {
		pi, pj := isPrinter(list[i]), isPrinter(list[j])
		if pi != pj {
			return pi
		}
		if (list[i].name != "") != (list[j].name != "") {
			return list[i].name != ""
		}
		if list[i].rssi != list[j].rssi {
			return list[i].rssi > list[j].rssi
		}
		return list[i].id < list[j].id
	})
	if a.pickSel == "" {
		for _, d := range list {
			if isPrinter(d) {
				a.pickSel = d.id
				break
			}
		}
	}
	status := fmt.Sprintf("Scan finished — %d device(s).", len(a.devices))
	if a.scanning {
		status = "Scanning for Bluetooth devices..."
	} else if len(a.devices) == 0 {
		status = "No devices found. Make sure the printer is on and in range, then Rescan."
	}
	return layout.UniformInset(16).Layout(gtx, func(gtx C) D {
		return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
			layout.Rigid(a.label("Select your printer", 15, cText, true)),
			layout.Rigid(layout.Spacer{Height: 6}.Layout),
			layout.Rigid(a.label(status, 11, cDim, false)),
			layout.Rigid(layout.Spacer{Height: 8}.Layout),
			layout.Rigid(func(gtx C) D {
				gtx.Constraints.Max.Y = gtx.Dp(220)
				gtx.Constraints.Min.Y = gtx.Dp(180)
				sz := image.Pt(gtx.Constraints.Max.X, gtx.Constraints.Min.Y)
				fill(gtx, cBG, sz)
				outline(gtx, cBorder, sz)
				gtx.Constraints = layout.Exact(sz)
				return material.List(a.th, &a.pkList).Layout(gtx, len(list), func(gtx C, i int) D {
					d := list[i]
					if d.btn.Clicked(gtx) {
						a.pickSel = d.id
					}
					return d.btn.Layout(gtx, func(gtx C) D {
						rowH := gtx.Dp(44)
						sz := image.Pt(gtx.Constraints.Max.X, rowH)
						if d.id == a.pickSel {
							fill(gtx, cAccent, sz)
						} else if d.btn.Hovered() {
							fill(gtx, cSurface, sz)
						}
						name, nc := d.name, cText
						if name == "" {
							name, nc = "Unnamed device", cDim
						}
						sig := "Signal unknown"
						if d.rssi != 0 {
							sig = fmt.Sprintf("Signal %d dBm", d.rssi)
						}
						layout.Inset{Left: 8, Top: 5}.Layout(gtx, func(gtx C) D {
							return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
								layout.Rigid(a.label(name, 13, nc, isPrinter(d))),
								layout.Rigid(a.label(sig, 10, cDim, false)),
							)
						})
						return D{Size: sz}
					})
				})
			}),
			layout.Rigid(layout.Spacer{Height: 8}.Layout),
			layout.Rigid(func(gtx C) D {
				c := material.CheckBox(a.th, &a.hideUnnam, "Hide unnamed devices")
				c.IconColor, c.Color = cAccent, cText
				return c.Layout(gtx)
			}),
			layout.Rigid(layout.Spacer{Height: 8}.Layout),
			layout.Rigid(func(gtx C) D {
				return layout.Flex{Spacing: layout.SpaceStart}.Layout(gtx,
					layout.Rigid(func(gtx C) D {
						gtx.Constraints.Max.X = gtx.Dp(90)
						return a.button(gtx, &a.pkRescan, "Rescan", 30, kFlat, !a.scanning)
					}),
					layout.Rigid(layout.Spacer{Width: 8}.Layout),
					layout.Rigid(func(gtx C) D {
						gtx.Constraints.Max.X = gtx.Dp(90)
						return a.button(gtx, &a.pkCancel, "Cancel", 30, kFlat, true)
					}),
					layout.Rigid(layout.Spacer{Width: 8}.Layout),
					layout.Rigid(func(gtx C) D {
						gtx.Constraints.Max.X = gtx.Dp(100)
						return a.button(gtx, &a.pkConnect, "Connect", 30, kAccent, a.pickSel != "")
					}),
				)
			}),
		)
	})
}
