// Package thermal implements the Core Innovations CTP500BR protocol, 1-bit
// rendering for its 384-dot print head, and the BLE link used to reach it.
package thermal

import (
	"encoding/hex"
	"fmt"
	"strings"
)

// BLE identifiers used by the printer (ISSC transparent-UART style service).
const (
	ServiceUUID = "49535343-fe7d-4ae5-8fa9-9fafd205e455"
	TxUUID      = "49535343-8841-43f4-a8d4-ecbe34729bb3"
	RxUUID      = "49535343-1e4d-4bd9-ba61-23c647249616"
)

// Crc8 is CRC-8 (poly 0x07, init 0) over the payload of a command frame.
func Crc8(data []byte) byte {
	var c byte
	for _, b := range data {
		c ^= b
		for i := 0; i < 8; i++ {
			if c&0x80 != 0 {
				c = c<<1 ^ 0x07
			} else {
				c <<= 1
			}
		}
	}
	return c
}

// Frame builds 51 78 <cmd> 00 <len16 LE> <payload> <crc8(payload)> FF.
func Frame(cmd byte, payload ...byte) []byte {
	f := make([]byte, 0, 8+len(payload))
	f = append(f, 0x51, 0x78, cmd, 0x00, byte(len(payload)), byte(len(payload)>>8))
	f = append(f, payload...)
	return append(f, Crc8(payload), 0xFF)
}

// Command frames. Bytes are device-specific and unchanged from the C# app.
func GetInfo() []byte      { return Frame(0xA8, 0x00) }
func GetBattery() []byte   { return Frame(0xBA, 0x00) }
func GetStatus() []byte    { return Frame(0xA3, 0x00) }
func GetDevID() []byte     { return Frame(0xBB, 0x01) }
func StopPrint() []byte    { return Frame(0xA6, 0x05) }
func PrintModeImg() []byte { return Frame(0xBE, 0x00) }
func PrintModeText() []byte {
	return Frame(0xBE, 0x01)
}
func SelfTest() []byte {
	return Frame(0xA6, 0xAA, 0x55, 0x17, 0x38, 0x44, 0x5F, 0x5F, 0x5F, 0x44, 0x38, 0x2C)
}
func EscInit() []byte { return []byte{0x1B, 0x40} }

// FeedLines feeds paper by n dot rows (little-endian payload).
func FeedLines(n int) []byte {
	if n < 1 {
		n = 1
	}
	if n > 0xFFFF {
		n = 0xFFFF
	}
	return Frame(0xA1, byte(n), byte(n>>8))
}

// SetDensity takes a level 0 (lightest) .. 4 (darkest).
func SetDensity(level int) []byte {
	if level < 0 {
		level = 0
	}
	if level > 4 {
		level = 4
	}
	return Frame(0xA4, 0x31+byte(level))
}

// Status is decoded from the standard ESC/POS real-time status replies (DLE EOT n),
// which this printer answers. Idle replies seen on a CTP500BR: n=1 0x16, n=2 0x12, n=3 0x12, n=4 0x10.
type Status struct{ PaperOut, PaperLow, CoverOpen, Error bool }

func (s Status) OK() bool { return !s.PaperOut && !s.CoverOpen && !s.Error }

// DecodeStatus maps the replies to DLE EOT 2 (offline cause), 3 (error cause) and 4 (paper sensor).
func DecodeStatus(offline, errCause, paper byte) Status {
	return Status{
		CoverOpen: offline&0x04 != 0,
		PaperOut:  offline&0x20 != 0 || paper&0x60 != 0,
		PaperLow:  paper&0x0C != 0,
		Error:     offline&0x40 != 0 || errCause&0x60 != 0,
	}
}

// ParseBattery returns the battery percent from a vendor GET_BATTERY reply. The printer
// has not been observed to answer this frame, so the GUI does not rely on it.
func ParseBattery(d []byte) (int, bool) {
	if len(d) >= 7 && d[0] == 0x51 && d[1] == 0x78 && d[2] == 0xBA {
		return int(d[6]), true
	}
	return 0, false
}

// DescribeResponse formats a notification for the console.
func DescribeResponse(d []byte) string {
	if len(d) == 0 {
		return "RX: (empty)"
	}
	h := strings.ToUpper(hex.EncodeToString(d))
	switch {
	case len(d) >= 5 && d[0] == 0x1B && d[1] == 0x1F && d[2] == 0xFE,
		len(d) >= 9 && d[0] == 0xFA && d[1] == 0x01,
		len(d) >= 3 && d[0] == 0x1B && d[1] == 0x1E && d[2] == 0x01:
		return "RX SYSTEM: " + h
	case len(d) >= 2 && d[0] == 0x45 && d[1] == 0x52:
		return "RX ERROR: " + h
	case len(d) >= 3 && d[0] == 0x51 && d[1] == 0x78:
		names := map[byte]string{0xA8: "GET_INFO", 0xBA: "GET_BATTERY", 0xA3: "GET_STATUS", 0xBB: "GET_DEV_ID",
			0xA4: "SET_DENSITY", 0xA1: "FEED", 0xBE: "SET_PRINT_MODE", 0xA6: "SELFTEST/STOP",
			0xBD: "FEED_PAPER", 0xAF: "SET_ENERGY", 0xAC: "CHECK_LABEL"}
		n, ok := names[d[2]]
		if !ok {
			n = fmt.Sprintf("0x%02X", d[2])
		}
		return fmt.Sprintf("RX [%s]: %s", n, h)
	}
	return "RX: " + h
}
