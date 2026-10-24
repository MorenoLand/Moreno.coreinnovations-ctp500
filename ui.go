package main

import (
	"image"
	"image/color"
	"os"
	"path/filepath"

	"gioui.org/f32"
	"gioui.org/font"
	"gioui.org/font/gofont"
	"gioui.org/font/opentype"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/text"
	"gioui.org/unit"
	"gioui.org/widget"
	"gioui.org/widget/material"
)

type (
	C = layout.Context
	D = layout.Dimensions
)

// Palette — identical to the C# build's theme.
var (
	cBG      = rgb(25, 25, 28)
	cPanel   = rgb(37, 37, 40)
	cSurface = rgb(45, 45, 48)
	cBorder  = rgb(63, 63, 70)
	cText    = rgb(212, 212, 216)
	cDim     = rgb(122, 122, 130)
	cAccent  = rgb(0, 122, 204)
	cGreen   = rgb(106, 204, 88)
	cRed     = rgb(228, 88, 88)
	cOrange  = rgb(230, 160, 50)
	cWhite   = rgb(255, 255, 255)
)

func rgb(r, g, b uint8) color.NRGBA { return color.NRGBA{R: r, G: g, B: b, A: 255} }

func lighten(c color.NRGBA, n uint8) color.NRGBA {
	add := func(v uint8) uint8 {
		if int(v)+int(n) > 255 {
			return 255
		}
		return v + n
	}
	return color.NRGBA{add(c.R), add(c.G), add(c.B), c.A}
}

func fill(gtx C, c color.NRGBA, size image.Point) {
	defer clip.Rect{Max: size}.Push(gtx.Ops).Pop()
	paint.Fill(gtx.Ops, c)
}

func outline(gtx C, c color.NRGBA, size image.Point) {
	defer clip.Stroke{Path: clip.Rect{Max: size}.Path(), Width: 1}.Op().Push(gtx.Ops).Pop()
	paint.Fill(gtx.Ops, c)
}

func strokeLine(gtx C, c color.NRGBA, w float32, pts ...f32.Point) {
	var p clip.Path
	p.Begin(gtx.Ops)
	p.MoveTo(pts[0])
	for _, q := range pts[1:] {
		p.LineTo(q)
	}
	defer clip.Stroke{Path: p.End(), Width: w}.Op().Push(gtx.Ops).Pop()
	paint.Fill(gtx.Ops, c)
}

func fillTriangle(gtx C, c color.NRGBA, a, b, d f32.Point) {
	var p clip.Path
	p.Begin(gtx.Ops)
	p.MoveTo(a)
	p.LineTo(b)
	p.LineTo(d)
	p.Close()
	defer clip.Outline{Path: p.End()}.Op().Push(gtx.Ops).Pop()
	paint.Fill(gtx.Ops, c)
}

// fonts builds the shaper collection: Segoe UI (as the C# build), Consolas for the console,
// Segoe UI Symbol for glyph fallback, with Go fonts if the system ones are missing.
func newTheme() *material.Theme {
	dir := filepath.Join(os.Getenv("WINDIR"), "Fonts")
	var faces []font.FontFace
	add := func(file, typeface string, weight font.Weight) bool {
		b, err := os.ReadFile(filepath.Join(dir, file))
		if err != nil {
			return false
		}
		f, err := opentype.Parse(b)
		if err != nil {
			return false
		}
		faces = append(faces, font.FontFace{Font: font.Font{Typeface: font.Typeface(typeface), Weight: weight}, Face: f})
		return true
	}
	add("segoeui.ttf", "Segoe UI", font.Normal)
	add("segoeuib.ttf", "Segoe UI", font.Bold)
	add("seguisym.ttf", "Segoe UI Symbol", font.Normal)
	add("consola.ttf", "Consolas", font.Normal)
	faces = append(faces, gofont.Collection()...)
	th := material.NewTheme()
	th.Shaper = text.NewShaper(text.NoSystemFonts(), text.WithCollection(faces))
	th.Face = "Segoe UI, Segoe UI Symbol"
	th.TextSize = 12
	th.Palette = material.Palette{Fg: cText, Bg: cBG, ContrastBg: cAccent, ContrastFg: cWhite}
	return th
}

// Button kinds.
const (
	kFlat = iota
	kAccent
)

func (a *App) label(txt string, size float32, col color.NRGBA, bold bool) layout.Widget {
	return func(gtx C) D {
		l := material.Label(a.th, unit.Sp(size), txt)
		l.Color = col
		if bold {
			l.Font.Weight = font.Bold
		}
		return l.Layout(gtx)
	}
}

func (a *App) button(gtx C, c *widget.Clickable, label string, h int, kind int, enabled bool) D {
	if !enabled {
		gtx = gtx.Disabled()
	}
	return c.Layout(gtx, func(gtx C) D {
		sz := image.Pt(gtx.Constraints.Max.X, gtx.Dp(unit.Dp(h)))
		bg, fg := cSurface, cText
		if kind == kAccent {
			bg, fg = cAccent, cWhite
		}
		if !enabled {
			fg = cDim
			if kind == kAccent {
				bg = rgb(30, 70, 105)
			}
		} else if c.Pressed() {
			bg = lighten(bg, 28)
		} else if c.Hovered() {
			bg = lighten(bg, 12)
		}
		fill(gtx, bg, sz)
		if kind != kAccent {
			outline(gtx, cBorder, sz)
		}
		gtx.Constraints = layout.Exact(sz)
		layout.Center.Layout(gtx, a.label(label, 12, fg, kind == kAccent))
		return D{Size: sz}
	})
}

// bordered draws w inside a surface-coloured box with a 1px border.
func bordered(gtx C, bg color.NRGBA, w layout.Widget) D {
	m := op.Record(gtx.Ops)
	dims := w(gtx)
	call := m.Stop()
	fill(gtx, bg, dims.Size)
	call.Add(gtx.Ops)
	outline(gtx, cBorder, dims.Size)
	return dims
}

func (a *App) header(t string) layout.Widget {
	return func(gtx C) D {
		return layout.Inset{Top: 12, Bottom: 6}.Layout(gtx, func(gtx C) D {
			return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
				layout.Rigid(func(gtx C) D {
					sz := image.Pt(gtx.Dp(20), gtx.Dp(2))
					fill(gtx, cAccent, sz)
					return D{Size: image.Pt(sz.X, sz.Y+gtx.Dp(4))}
				}),
				layout.Rigid(a.label(t, 10, cDim, true)),
			)
		})
	}
}

// dropdown is a minimal combo box: click to open a list drawn above other content.
type dropdown struct {
	items []string
	sel   int
	open  bool
	btn   widget.Clickable
	opts  []widget.Clickable
}

func newDropdown(items ...string) *dropdown {
	return &dropdown{items: items, opts: make([]widget.Clickable, len(items))}
}

func (d *dropdown) layout(a *App, gtx C) D {
	if d.btn.Clicked(gtx) {
		d.open = !d.open
	}
	h := gtx.Dp(30)
	dims := d.btn.Layout(gtx, func(gtx C) D {
		sz := image.Pt(gtx.Constraints.Max.X, h)
		bg := cSurface
		if d.btn.Hovered() {
			bg = lighten(bg, 10)
		}
		fill(gtx, bg, sz)
		outline(gtx, cBorder, sz)
		layout.Inset{Left: 8, Top: 6}.Layout(gtx, a.label(d.items[d.sel], 12, cText, false))
		cx, cy := float32(sz.X-16), float32(h)/2
		fillTriangle(gtx, cDim, f32.Pt(cx-4, cy-2), f32.Pt(cx+4, cy-2), f32.Pt(cx, cy+3))
		return D{Size: sz}
	})
	if d.open {
		m := op.Record(gtx.Ops)
		off := op.Offset(image.Pt(0, h)).Push(gtx.Ops)
		oh := gtx.Dp(28)
		y := 0
		for i := range d.items {
			if d.opts[i].Clicked(gtx) {
				d.sel, d.open = i, false
			}
			st := op.Offset(image.Pt(0, y)).Push(gtx.Ops)
			d.opts[i].Layout(gtx, func(gtx C) D {
				sz := image.Pt(dims.Size.X, oh)
				bg := cPanel
				if d.opts[i].Hovered() || i == d.sel {
					bg = cAccent
				}
				fill(gtx, bg, sz)
				layout.Inset{Left: 8, Top: 5}.Layout(gtx, a.label(d.items[i], 12, cText, false))
				return D{Size: sz}
			})
			st.Pop()
			y += oh
		}
		outlineSz := image.Pt(dims.Size.X, y)
		outline(gtx, cBorder, outlineSz)
		off.Pop()
		op.Defer(gtx.Ops, m.Stop())
	}
	return dims
}
