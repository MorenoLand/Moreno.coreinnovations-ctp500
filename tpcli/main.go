// tpcli is a small command-line companion: scan for printers, render a preview PNG,
// query status, or print text/images without the GUI.
package main

import (
	"context"
	"encoding/hex"
	"fmt"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	"image/png"
	"os"
	"strings"
	"time"

	_ "golang.org/x/image/bmp"

	"ctp500driver/thermal"
)

func usage() {
	fmt.Println(`usage:
  tpcli scan [seconds]
  tpcli render out.png "text" [size]      write the exact 1-bit output as a PNG
  tpcli probe <id>                        send query frames and show replies
  tpcli send <id> <hex...>                send raw bytes and show replies
  tpcli print <id> "text" | file.png`)
}

func main() {
	if len(os.Args) < 2 {
		usage()
		return
	}
	logf := func(s string) { fmt.Println(s) }
	switch os.Args[1] {
	case "scan":
		secs := 8
		if len(os.Args) > 2 {
			fmt.Sscanf(os.Args[2], "%d", &secs)
		}
		seen := map[string]thermal.Found{}
		err := thermal.Scan(context.Background(), time.Duration(secs)*time.Second, logf, func(f thermal.Found) {
			old := seen[f.ID]
			if f.Name == "" {
				f.Name = old.Name
			}
			seen[f.ID] = f
		})
		if err != nil {
			fmt.Println("error:", err)
		}
		for _, f := range seen {
			fmt.Printf("%s  %-24q  %d dBm\n", f.ID, f.Name, f.RSSI)
		}
	case "render":
		if len(os.Args) < 4 {
			usage()
			return
		}
		size := 24
		if len(os.Args) > 4 {
			fmt.Sscanf(os.Args[4], "%d", &size)
		}
		doc := &thermal.Doc{Texts: []thermal.TextLayer{{Text: os.Args[3], Size: size}}}
		f, _ := os.Create(os.Args[2])
		defer f.Close()
		png.Encode(f, thermal.Render(doc, 128, true))
	case "probe", "send", "print":
		if len(os.Args) < 3 {
			usage()
			return
		}
		l := &thermal.Link{Log: logf}
		if err := l.Connect(os.Args[2]); err != nil {
			fmt.Println("error:", err)
			os.Exit(1)
		}
		defer l.Disconnect()
		switch os.Args[1] {
		case "probe":
			qs := [][]byte{thermal.GetStatus(), thermal.GetBattery()}
			for n := byte(1); n <= 8; n++ {
				qs = append(qs, []byte{0x10, 0x04, n})
			}
			for _, n := range []byte{1, 2, 3, 49, 65, 66, 67, 68, 69} {
				qs = append(qs, []byte{0x1D, 0x49, n})
			}
			qs = append(qs, []byte{0x1D, 0x72, 0x01}, []byte{0x1D, 0x72, 0x02}, []byte{0x1B, 0x76})
			for _, q := range qs {
				fmt.Printf("TX % X\n", q)
				l.Send(q)
				time.Sleep(500 * time.Millisecond)
			}
		case "send":
			b, err := hex.DecodeString(strings.Join(os.Args[3:], ""))
			if err != nil {
				fmt.Println(err)
				return
			}
			l.Send(b)
			time.Sleep(time.Second)
		case "print":
			doc := &thermal.Doc{}
			arg := strings.Join(os.Args[3:], " ")
			if f, err := os.Open(arg); err == nil {
				doc.Source, _, _ = image.Decode(f)
				f.Close()
			} else {
				doc.Texts = []thermal.TextLayer{{Text: arg, Size: 24}}
			}
			if err := l.Print(doc, 128, true, nil); err != nil {
				fmt.Println("error:", err)
			}
			time.Sleep(time.Second)
		}
	default:
		usage()
	}
}
