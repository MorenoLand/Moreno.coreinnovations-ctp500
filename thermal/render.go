package thermal

import (
	"image"
	"image/color"
	"os"
	"path/filepath"
	"strings"
	"sync"

	"golang.org/x/image/draw"
	"golang.org/x/image/font"
	"golang.org/x/image/font/gofont/gobold"
	"golang.org/x/image/font/gofont/goregular"
	"golang.org/x/image/font/opentype"
	"golang.org/x/image/math/fixed"
)

// PaperW is the print head width in dots (48 mm at 200 DPI).
const PaperW = 384

const pad = 8

// Text alignment values.
const (
	AlignLeft = iota
	AlignCenter
	AlignRight
)

// TextLayer is one block of wrapped text.
type TextLayer struct {
	Text  string
	Size  int
	Align int
	Bold  bool
}

// Doc is what gets composed onto the paper: an optional image on top, then text blocks.
type Doc struct {
	Source image.Image
	Texts  []TextLayer
	Invert bool
}

var (
	fontMu    sync.Mutex
	regular   *opentype.Font
	bold      *opentype.Font
	faceCache = map[[2]int]font.Face{}
)

func loadFonts() {
	if regular != nil {
		return
	}
	dir := filepath.Join(os.Getenv("WINDIR"), "Fonts")
	load := func(name string, fallback []byte) *opentype.Font {
		if b, err := os.ReadFile(filepath.Join(dir, name)); err == nil {
			if f, err := opentype.Parse(b); err == nil {
				return f
			}
		}
		f, _ := opentype.Parse(fallback)
		return f
	}
	regular = load("segoeui.ttf", goregular.TTF)
	bold = load("segoeuib.ttf", gobold.TTF)
}

func face(size int, isBold bool) font.Face {
	fontMu.Lock()
	defer fontMu.Unlock()
	loadFonts()
	k := [2]int{size, 0}
	f := regular
	if isBold {
		k[1], f = 1, bold
	}
	if fc, ok := faceCache[k]; ok {
		return fc
	}
	fc, _ := opentype.NewFace(f, &opentype.FaceOptions{Size: float64(size), DPI: 72, Hinting: font.HintingNone})
	faceCache[k] = fc
	return fc
}

func measure(f font.Face, s string) float64 {
	return float64(font.MeasureString(f, s)) / 64
}

func wrap(text string, f font.Face, maxW float64) []string {
	var out []string
	for _, para := range strings.Split(strings.ReplaceAll(text, "\r", ""), "\n") {
		cur := ""
		for _, word := range strings.Split(para, " ") {
			test := word
			if cur != "" {
				test = cur + " " + word
			}
			if cur == "" || measure(f, test) <= maxW {
				cur = test
			} else {
				out = append(out, cur)
				cur = word
			}
		}
		out = append(out, cur)
	}
	return out
}

func lineHeight(f font.Face) int {
	m := f.Metrics()
	return int((m.Height + 63) >> 6)
}

// Render composes the document at 384 dots wide and returns a bitmap whose
// pixels are strictly 0 (burn) or 255 (blank). Preview and print share it.
func Render(d *Doc, threshold int, dither bool) *image.Gray {
	imgRows := 0
	var scaled *image.RGBA
	if d.Source != nil {
		b := d.Source.Bounds()
		if b.Dx() > 0 && b.Dy() > 0 {
			imgRows = int(float64(b.Dy())*float64(PaperW)/float64(b.Dx()) + 0.5)
			if imgRows < 1 {
				imgRows = 1
			}
			scaled = image.NewRGBA(image.Rect(0, 0, PaperW, imgRows))
			draw.Draw(scaled, scaled.Bounds(), image.White, image.Point{}, draw.Src)
			draw.CatmullRom.Scale(scaled, scaled.Bounds(), d.Source, b, draw.Over, nil)
		}
	}
	type block struct {
		t     TextLayer
		lines []string
		f     font.Face
		lh    int
	}
	var blocks []block
	height := imgRows
	for _, t := range d.Texts {
		f := face(t.Size, t.Bold)
		lines := wrap(t.Text, f, PaperW-pad*2)
		lh := lineHeight(f)
		blocks = append(blocks, block{t, lines, f, lh})
		height += pad + lh*len(lines)
	}
	if len(blocks) > 0 {
		height += pad
	}
	if height < 64 {
		height = 64
	}

	canvas := image.NewRGBA(image.Rect(0, 0, PaperW, height))
	draw.Draw(canvas, canvas.Bounds(), image.White, image.Point{}, draw.Src)
	if scaled != nil {
		draw.Draw(canvas, scaled.Bounds(), scaled, image.Point{}, draw.Src)
	}
	y := imgRows
	for _, b := range blocks {
		y += pad
		asc := int(b.f.Metrics().Ascent >> 6)
		for _, ln := range b.lines {
			w := measure(b.f, ln)
			x := float64(pad)
			switch b.t.Align {
			case AlignCenter:
				x = (PaperW - w) / 2
			case AlignRight:
				x = PaperW - pad - w
			}
			dr := font.Drawer{Dst: canvas, Src: image.Black, Face: b.f,
				Dot: fixed.Point26_6{X: fixed.Int26_6(x * 64), Y: fixed.I(y + asc)}}
			dr.DrawString(ln)
			y += b.lh
		}
	}

	// Luma (30/59/11) -> threshold, with error diffusion on image rows only so text stays crisp.
	buf := make([]float32, PaperW*height)
	for yy := 0; yy < height; yy++ {
		for xx := 0; xx < PaperW; xx++ {
			o := canvas.PixOffset(xx, yy)
			p := canvas.Pix
			buf[yy*PaperW+xx] = float32((int(p[o])*30 + int(p[o+1])*59 + int(p[o+2])*11) / 100)
		}
	}
	out := image.NewGray(image.Rect(0, 0, PaperW, height))
	th := float32(threshold)
	for yy := 0; yy < height; yy++ {
		for xx := 0; xx < PaperW; xx++ {
			i := yy*PaperW + xx
			old := buf[i]
			var nw float32 = 255
			if old < th {
				nw = 0
			}
			if dither && yy < imgRows {
				e := old - nw
				if xx+1 < PaperW {
					buf[i+1] += e * 7 / 16
				}
				if yy+1 < height {
					if xx > 0 {
						buf[i+PaperW-1] += e * 3 / 16
					}
					buf[i+PaperW] += e * 5 / 16
					if xx+1 < PaperW {
						buf[i+PaperW+1] += e * 1 / 16
					}
				}
			}
			black := nw == 0
			if d.Invert {
				black = !black
			}
			v := uint8(255)
			if black {
				v = 0
			}
			out.Pix[yy*out.Stride+xx] = v
		}
	}
	return out
}

// Pack converts a 0/255 bitmap to the printer's 1 bit-per-dot rows (MSB first, 1 = burn).
func Pack(g *image.Gray) (bits []byte, bytesPerRow, height int) {
	height = g.Bounds().Dy()
	bytesPerRow = PaperW / 8
	bits = make([]byte, bytesPerRow*height)
	for y := 0; y < height; y++ {
		for x := 0; x < PaperW; x++ {
			if g.Pix[y*g.Stride+x] < 128 {
				bits[y*bytesPerRow+x/8] |= 0x80 >> uint(x%8)
			}
		}
	}
	return
}

// Preview returns the exact 1-bit output tinted for on-screen display.
func Preview(g *image.Gray) *image.NRGBA {
	out := image.NewNRGBA(g.Bounds())
	ink, paper := color.NRGBA{20, 20, 20, 255}, color.NRGBA{250, 250, 245, 255}
	for y := 0; y < g.Bounds().Dy(); y++ {
		for x := 0; x < PaperW; x++ {
			c := paper
			if g.Pix[y*g.Stride+x] < 128 {
				c = ink
			}
			out.SetNRGBA(x, y, c)
		}
	}
	return out
}
