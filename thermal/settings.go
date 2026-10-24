package thermal

import (
	"encoding/json"
	"os"
	"path/filepath"
)

// Settings persist in %AppData%\ThermalPrint\settings.json — the same file and
// field names the C# build used, so existing settings carry over.
type Settings struct {
	LastDevice *string `json:"LastDevice"`
	Threshold  int     `json:"Threshold"`
	Dither     bool    `json:"Dither"`
	Invert     bool    `json:"Invert"`
	Density    int     `json:"Density"`
}

func settingsPath() string {
	dir, _ := os.UserConfigDir() // %AppData% on Windows
	return filepath.Join(dir, "ThermalPrint", "settings.json")
}

func LoadSettings() *Settings {
	s := &Settings{Threshold: 128, Dither: true, Density: 3}
	if b, err := os.ReadFile(settingsPath()); err == nil {
		_ = json.Unmarshal(b, s)
	}
	s.Threshold = max(0, min(255, s.Threshold))
	s.Density = max(1, min(5, s.Density))
	return s
}

func (s *Settings) Save() {
	p := settingsPath()
	_ = os.MkdirAll(filepath.Dir(p), 0o755)
	if b, err := json.Marshal(s); err == nil {
		_ = os.WriteFile(p, b, 0o644)
	}
}
