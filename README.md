# 🎧 ImplicateX.TinyCLR.Drivers.Decoder.Vs1053

TinyCLR driver for the VS1053B audio decoder with strict SCI/SDI separation, manual chip‑select handling, deterministic DREQ‑based streaming — and a modern media preprocessor pipeline for clean, extensible format handling.
---

## ⚙️ Features

- 🔀 Separate SPI domains:
  - **SCI (control):** 250 kHz startup/control communication
  - **SDI (data):** switched to **5 MHz** after initialization for streaming
- 🎚️ Manual chip-select via GPIO (no automatic SPI CS)
- 📡 Stable DREQ synchronization before SDI writes
- 🔁 Hardware reset + startup register configuration
- 🎵 MP3, WAV, FLAC, OGG, AAC, MID, and M4A playback
- 🔊 Sine test support for hardware validation
- 🎹 Optional real-time MIDI initialization and UART MIDI transport
- 🧩 Modular Media Preprocessor Architecture
  - Each audio format is handled by a dedicated processor
  - Clean separation of container parsing, patching, and stream normalization

-🧩 Media Preprocessor Architecture (New)
- Modern audio formats differ drastically in structure:
  - MP3 → linear frames
  - WAV → RIFF container
  - FLAC → patch‑based decoding
  - DSD → high‑throughput patch + custom clock (experimental)
  - M4A → MP4 container with AAC track extraction
  - AAC → ADTS/ADIF raw stream

Device class obtains a MediaPayLoad from the appropriate preprocessor based on file extension
---

## 📦 Requirements

- .NET Framework **4.8**
- TinyCLR OS packages (3.0.2.1000), including:
  - `GHIElectronics.TinyCLR.Devices.Gpio`
  - `GHIElectronics.TinyCLR.Devices.Spi`
  - `GHIElectronics.TinyCLR.Devices.Uart`
  - `GHIElectronics.TinyCLR.IO`
  - `GHIElectronics.TinyCLR.Native`
  - `GHIElectronics.TinyCLR.Core`

---

## 🔌 Hardware Wiring

Required signals:

- `CMD_CS` (SCI chip-select, GPIO-controlled)
- `DAT_CS` (SDI chip-select, GPIO-controlled)
- `DREQ`
- `RESET`
- Shared `SPI` bus (`SCK`, `MOSI`, optional `MISO`)

Optional for real-time MIDI boot mode:

- `GPIO0`
- `GPIO1`

---

## 📝 Important VS1053 Notes

- This driver intentionally uses **manual chip-select control** for stable SPI behavior.
- For VS1053 **real-time MIDI mode**, set boot pins **before reset sampling**:
  - `GPIO0 = LOW`
  - `GPIO1 = HIGH`
- Then apply hardware reset pulse.

---

## 🚀 Quick Start (Audio Playback)

```csharp
using GHIElectronics.TinyCLR.Pins;
using ImplicateX.TinyCLR.Drivers.Decoder.Vs1053;

namespace Vs1053App
{
	internal class Program
	{
		/// <summary>
		/// The device instance for the VS1053 audio decoder.
		/// </summary>
		private static Device device = null!;

		static void Main()
		{
			_ = new Storage();

			device = new Device(
				uartControllerName: FEZDuino.UartPort.Uart1,
				spiControllerName: FEZDuino.SpiBus.Spi6,
				cmdCsPinID: FEZDuino.GpioPin.PC4,
				datCsPinID: FEZDuino.GpioPin.PC5,
				dreqPinID: FEZDuino.GpioPin.PC6,
				resetPinID: FEZDuino.GpioPin.PC7,
				gpio0PinID: FEZDuino.GpioPin.PA1,
				gpio1PinID: FEZDuino.GpioPin.PA2 );

			device.Initialize();

			device.PlaySong( @"A:\Usa-Hymn.mid" );
			device.PlaySong( @"A:\Thank You for the Music.mp3" );
			device.PlaySong( @"A:\All by Myself.flac" );
			device.PlaySong( @"A:\sample-12s.wav" );
			device.PlaySong( @"A:\ogg_15s.ogg" );
			device.PlaySong( @"A:\aac_15s.aac" );
			device.PlaySong( @"A:\m4a_15s.m4a" );
		}
	}
}
```
## 🎹 MIDI (Optional)

Initialize UART MIDI (31,250 baud):
`InitializeMidi(string uartPortName)`
Send test sequence:
`SendUartTestSequence()`

Play MIDI file:
`PlayMidiFile(...)`

If `GPIO0/GPIO1` are available, the driver applies real-time MIDI boot pin levels before reset.

---

## 📚 Public API (Core)

`Initialize()`
`SetVolume(byte leftChannel, byte rightChannel)`
`PlayMp3(string filePath)`
`PlayWav(string filePath)`
`RunStartupSineTest(int durationMs = 3000, byte sineCode = 0x44)`
`StartSineTest(byte sineCode = 0x44)`
`StopSineTest()`

## 📄 License

See `license.txt`.