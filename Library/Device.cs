using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Provides low-level control of a VS1053 audio codec over SPI using manually controlled chip-select lines.
	/// </summary>
	/// <remarks>
	/// This driver separates SCI (command) and SDI (data) traffic across two SPI device instances and
	/// gates transfers with the DREQ pin to follow VS1053 timing requirements.
	/// </remarks>
	public partial class Device
	{
		private const bool EnableVerboseTrace = false;
		/// <summary>
		/// SCI/command channel index for the SPI device array.
		/// </summary>
		private const int CommandChannel = 0;
		/// <summary>
		/// SDI/data channel index for the SPI device array.
		/// </summary>
		private const int DataChannel = 1;
		/// <summary>
		/// Recommended clock frequency for the VS1053 chip in SCI_CLOCKF register.
		/// </summary>
		private const ushort RecommendedClockf = 0x6000;
		/// <summary>
		/// Internal decode clock used for DSD playback.
		/// Keep aligned with actual XTALI reference to avoid playback rate drift.
		/// </summary>
		private const ushort DsdClockf = RecommendedClockf;
		/// <summary>
		/// Startup sine frequency for the VS1053 chip.
		/// </summary>
		private const int StartupSineFrequency = 0x44;
		/// <summary>
		/// Startup SPI frequency for the VS1053 chip.
		/// </summary>
		private const int StartupSPIFrequency = 250_000;
		/// <summary>
		/// Default SDI/data SPI frequency for the VS1053 chip.
		/// </summary>
		private const int DataSPIFrequency = 5_000_000;
		/// <summary>
		/// Higher SDI/data SPI frequency used for DSD streams that require more sustained throughput.
		/// </summary>
		private const int DsdDataSPIFrequency = 12_000_000;
		/// <summary>
		/// End fill byte for baseline VS1053 paths.
		/// </summary>
		private const byte EndFillByte = 0x00;
		/// <summary>
		/// End fill byte required by VS1053 DSD patch path.
		/// </summary>
		private const byte DsdEndFillByte = 0x55;
		/// <summary>
		/// Chunk size for baseline data transfers to the VS1053 chip.
		/// </summary>
		private const int ChunkSize = 32;
		/// <summary>
		/// Moderate chunk size used by AAC/ADTS streaming to reduce DREQ polling overhead without overdriving SDI.
		/// </summary>
		private const int AacSdiChunkSize = 32;
		/// <summary>
		/// DREQ poll interval in milliseconds. Lower values increase throughput but may cause timing issues.
		/// For WAV playback (high bitrate), use 0 for busy-wait; for stability, use 1.
		/// </summary>
		private static int DreqPollIntervalMs = 0;
		/// <summary>
		/// GPIO pin for the SCI/command channel chip-select.
		/// </summary>
		private readonly GpioPin cmdCsPin;
		/// <summary>
		/// GPIO pin for the SDI/data channel chip-select.
		/// </summary>
		private readonly GpioPin datCsPin;
		/// <summary>
		/// GPIO pin for the DREQ/data request signal.
		/// </summary>
		private readonly GpioPin dreqPin;
		/// <summary>
		/// GPIO pin for the RESET signal.
		/// </summary>
		private readonly GpioPin resetPin;
		/// <summary>
		/// GPIO pin for the optional GPIO0 signal, which can be used for additional control or status monitoring.
		/// </summary>
		private readonly GpioPin gpio0Pin;
		/// <summary>
		/// GPIO pin for the optional GPIO1 signal, which can be used for additional control or status monitoring.
		/// </summary>
		private readonly GpioPin gpio1Pin;
		/// <summary>
		/// SPI device for the SCI/command channel.
		/// </summary>
		private readonly SpiDevice spiCmdDevice;
		/// <summary>
		/// SPI device for the SDI/data channel.
		/// </summary>
		private readonly SpiDevice spiDataDevice;

		/// <summary>
		/// Synchronization object for SPI operations.
		/// </summary>
		private readonly object spiSync = new();
		/// <summary>
		/// Buffer for command transactions to the VS1053 chip.
		/// </summary>
		private readonly byte[] cmdBuffer = new byte[ 4 ];
		/// <summary>
		/// Buffer for data blocks to the VS1053 chip.
		/// </summary>
		private readonly byte[] dataBlock = new byte[ 32 ];
		/// <summary>
		/// Buffer for single-byte transactions to the VS1053 chip.
		/// </summary>
		private readonly byte[] singleByteBuffer = new byte[ 1 ];
		/// <summary>
		/// Startup mode for the VS1053 chip.
		/// </summary>
		private readonly ushort startupMode;

		private readonly PatchEngine patchEngine;

		private enum Register : byte
		{
			/// <summary>
			/// SCI_MODE register address.
			/// </summary>
			Mode = 0x00,
			/// <summary>
			/// SCI_STATUS register address.
			/// </summary>
			Status = 0x01,
			/// <summary>
			/// SCI_BASS register address.
			/// </summary>
			BassTreble = 0x02,
			/// <summary>
			/// SCI_CLOCKF register address.
			/// </summary>
			ClockFrequency = 0x03,
			/// <summary>
			/// SCI_DECODE_TIME register address.
			/// </summary>
			DecodeTime = 0x04,
			/// <summary>
			/// SCI_AUDATA register address.
			/// </summary>
			AudioData = 0x05,
			/// <summary>
			/// SCI_WRAM register address for writing and reading to/from the VS1053's internal RAM.
			/// </summary>
			WRAMWriteRead = 0x06,
			/// <summary>
			/// SCI_WRAMADDR register address for setting the base address of the VS1053's internal RAM.
			/// </summary>
			WRAMBaseAddress = 0x07,
			/// <summary>
			/// SCI_STREAM_HEADER_DATA_0 register address.
			/// </summary>
			StreamHeaderData0 = 0x08,
			/// <summary>
			/// SCI_STREAM_HEADER_DATA_1 register address.
			/// </summary>
			StreamHeaderData1 = 0x09,
			/// <summary>
			/// SCI_APP_START_ADDRESS register address.
			/// </summary>
			AppStartAddress = 0x0A,
			/// <summary>
			/// SCI_VOLUME register address.
			/// </summary>
			Volume = 0x0B,
			/// <summary>
			/// SCI_APP_CONTROL_0 register address.
			/// </summary>
			AppControl0 = 0x0C,
			/// <summary>
			/// SCI_APP_CONTROL_1 register address.
			/// </summary>
			AppControl1 = 0x0D,
			/// <summary>
			/// SCI_APP_CONTROL_2 register address.
			/// </summary>
			AppControl2 = 0x0E,
			/// <summary>
			/// SCI_APP_CONTROL_3 register address.
			/// </summary>
			AppControl3 = 0x0F
		}

		private static class Mode
		{
			/// <summary>
			/// Enables differential output mode for the analog output stage.
			/// </summary>
			public const ushort Differential = 0b000_0000_0000_0001;
			/// <summary>
			/// Allows MPEG Layers I and II decoding.
			/// </summary>
			public const ushort AllowMpeLayersIAndII = 0b0000_0000_0000_0010;
			/// <summary>
			/// Enables software reset of the VS1053 chip. This bit is self-clearing after the reset is complete.
			/// </summary>
			public const ushort SoftReset = 0b0000_0000_0000_0100;
			/// <summary>
			/// Cancels decoding of the current file.
			/// </summary>
			public const ushort CancelDecodingCurrentFile = 0b0000_0000_0000_1000;
			/// <summary>
			/// Sets the ear speaker to a low setting.
			/// </summary>
			public const ushort EarSpeakerLowSetting = 0b0000_0000_0001_0000;
			/// <summary>
			/// Allows SDI tests.
			/// </summary>
			public const ushort AllowSdiTests = 0b0000_0000_0010_0000;
			/// <summary>
			/// Enables stream mode.
			/// </summary>
			public const ushort StreamMode = 0b0000_0000_0100_0000;
			/// <summary>
			/// Sets the ear speaker to a high setting.
			/// </summary>
			public const ushort EarSpeakerHighSetting = 0b0000_0000_1000_0000;
			/// <summary>
			/// Sets the DCLK active edge.
			/// </summary>
			public const ushort DCLKActiveEdge = 0b0000_0001_0000_0000;
			/// <summary>
			/// Sets the bit order to MSb first.
			/// </summary>
			public const ushort BitOrderMSbFirst = 0b0000_0010_0000_0000;
			/// <summary>
			/// Shares the chip select.
			/// </summary>
			public const ushort ShareChipSelect = 0b0000_0100_0000_0000;
			/// <summary>
			/// Indicates a new SDI.
			/// </summary>
			public const ushort SdiNew = 0b0000_1000_0000_0000;
			/// <summary>
			/// Indicates that PCM recording is active.
			/// </summary>
			public const ushort PCMRecordingActive = 0b0001_0000_0000_0000;
			/// <summary>
			/// No specific mode is set.
			/// </summary>
			public const ushort None = 0b0010_0000_0000_0000;
			/// <summary>
			/// Selects Line 1.
			/// </summary>
			public const ushort Line1Selector = 0b0100_0000_0000_0000;
			/// <summary>
			/// SM_CLK_RANGE activates a clock divider in the XTAL input. <br/>
			/// When SM_CLK_RANGE is set, the clock is divided by 2 at the input.<br/>
			/// From the chip’s point of view e.g. 24 MHz becomes 12 MHz.<br/>
			/// SM_CLK_RANGE should be set as soon as possible after a chip reset.
			/// </summary>
			public const ushort InputClockRange = 0b1000_0000_0000_0000;

		}

		private static class Status
		{
			/// <summary>
			/// Reference voltage selection
			/// <list type="table">
			/// <item>0 = 1.23 V</item>
			/// <item>1 = 1.65 V</item>
			/// </list>
			/// </summary>
			public const ushort ReferenceVoltageSelection = 0b0000_0000_0000_0001;
			/// <summary>
			/// SS_AD_CLOCK can be set to divide the AD modulator frequency by 2 if XTALI/2 is too much.
			/// <list type="table">
			/// <item>0 = 6 MHz</item>
			/// <item>1 = 3 MHz</item>
			/// </list>
			/// </summary>
			public const ushort ClockSelection = 0b0000_0000_0000_0010;
			/// <summary>
			/// SS_APDOWN1 controls internal analog	powerdown.
			/// These bit are meant to be used by the system firmware only.
			/// </summary>
			public const ushort AnalogInternalPowerdown = 0b0000_0000_0000_0100;
			/// <summary>
			/// SS_APDOWN2 controls analog driver powerdown.
			/// </summary>
			public const ushort AnalogDriverPowerdown = 0b0000_0000_0000_1000;
			/// <summary>
			/// VersionMask is a 4-bit field that indicates the silicon version of the VS1053 chip.
			/// </summary>
			public const ushort VersionMask = 0b0000_0000_1111_0000;
			/// <summary>
			/// Overload is set when the analog output stage is overloaded.<br />
			/// The overload condition can be cleared by a soft reset or by clearing this bit in the SCI_MODE register.
			/// </summary>
			public const ushort Overload = 0b0000_0100_0000_0000;
			/// <summary>
			/// OverloadDisabled is set when the analog output stage overload is disabled.
			/// </summary>
			public const ushort OverloadDisabled = 0b0000_1000_0000_0000;
			/// <summary>
			/// SwingMask is a 3-bit field that indicates the swing level of the analog output stage.
			/// </summary>
			public const ushort SwingMask = 0b0111_0000_0000_0000;
			/// <summary>
			/// SS_DO_NOT_JUMP is set when a WAV, Ogg Vorbis, WMA, MP4, or AAC-ADIF header 
			/// is being decoded and jumping to another location in the file is not allowed.
			/// If you use soft reset or cancel, clear this bit yourself or it can be accidentally left set.
			/// </summary>
			public const ushort DecodedHeader = 0b1000_0000_0000_0000;
		}

		/// <summary>
		/// Creates a VS1053 driver instance and prepares GPIO/SPI resources for SCI (command) and SDI (data) communication.
		/// </summary>
		/// <param name="spiControllerName">SPI controller name used to open the VS1053 bus.</param>
		/// <param name="cmdCsPinID">GPIO pin number used as manual chip-select for the SCI/command channel.</param>
		/// <param name="datCsPinID">GPIO pin number used as manual chip-select for the SDI/data channel.</param>
		/// <param name="dreqPinID">GPIO pin number connected to VS1053 DREQ (data request) signal.</param>
		/// <param name="resetPinID">GPIO pin number connected to VS1053 hardware reset.</param>
		/// <remarks>
		/// The driver intentionally uses <see cref="SpiChipSelectType.None"/> and controls chip-select lines manually
		/// via GPIO to ensure stable VS1053 transactions on both channels.
		/// Startup SPI frequency is applied to both devices; higher SDI speed is configured later in <see cref="Initialize"/>.
		/// </remarks>
		public Device(
			string spiControllerName,
			int cmdCsPinID,
			int datCsPinID,
			int dreqPinID,
			int resetPinID,
			int gpio0PinID = -1,
			int gpio1PinID = -1 )
		{
			this.patchEngine = new PatchEngine( this );

			var gpioController = GpioController.GetDefault();
			var spiController = SpiController.FromName( spiControllerName );

			this.cmdCsPin = gpioController.OpenPin( cmdCsPinID );
			this.datCsPin = gpioController.OpenPin( datCsPinID );
			this.dreqPin = gpioController.OpenPin( dreqPinID );
			this.resetPin = gpioController.OpenPin( resetPinID );

			if( gpio0PinID != -1 )
			{
				this.gpio0Pin = gpioController.OpenPin( gpio0PinID );
				this.gpio0Pin.SetDriveMode( GpioPinDriveMode.InputPullDown );
			}

			if( gpio1PinID != -1 )
			{
				this.gpio1Pin = gpioController.OpenPin( gpio1PinID );
				this.gpio1Pin.SetDriveMode( GpioPinDriveMode.InputPullDown );
			}

			this.cmdCsPin.SetDriveMode( GpioPinDriveMode.Output );
			this.datCsPin.SetDriveMode( GpioPinDriveMode.Output );
			this.dreqPin.SetDriveMode( GpioPinDriveMode.Input );
			this.resetPin.SetDriveMode( GpioPinDriveMode.Output );

			DisableSci();
			DisableSdi();

			this.resetPin.Write( GpioPinValue.High );

			var spiSettings = new SpiConnectionSettings[]
			{
				new()
				{
					ChipSelectType = SpiChipSelectType.None,
					ClockFrequency = StartupSPIFrequency,
					Mode = SpiMode.Mode0
				},
				new()
				{
					ChipSelectType = SpiChipSelectType.None,
					ClockFrequency = StartupSPIFrequency,
					Mode = SpiMode.Mode0
				}
			};

			this.spiCmdDevice = spiController.GetDevice( spiSettings[ CommandChannel ] );
			this.spiDataDevice = spiController.GetDevice( spiSettings[ DataChannel ] );

			this.startupMode = Mode.SdiNew | Mode.AllowMpeLayersIAndII;
		}

		/// <summary>
		/// Resets and configures the codec for normal playback operation.
		/// </summary>
		/// <remarks>
		/// Performs a hardware reset, applies initial SCI register configuration, then increases SDI SPI clock
		/// from startup speed to streaming speed for audio data transfer.
		/// </remarks>
		public void Initialize()
		{
			ResetHardware();
			ConfigureStartupRegisters();

			this.spiDataDevice.ConnectionSettings.ClockFrequency = DataSPIFrequency;
		}

		/// <summary>
		/// Sets output volume for left and right channels.
		/// </summary>
		/// <param name="leftChannel">Left channel volume from 0 (mute) to 255 (max).</param>
		/// <param name="rightChannel">Right channel volume from 0 (mute) to 255 (max).</param>
		/// <remarks>
		/// VS1053 SCI volume register is inverse-scaled; this method translates user-friendly values internally.
		/// </remarks>
		public void SetVolume( byte leftChannel, byte rightChannel )
		{
			ushort volume = ( ushort )( ( 255 - leftChannel ) << 8 | ( 255 - rightChannel ) );
			SciWrite( Register.Volume, volume );
		}

		/// <summary>
		/// Runs the VS1053 startup sine test for a fixed duration.
		/// </summary>
		/// <param name="durationMs">Duration in milliseconds. Default is 3000.</param>
		/// <param name="sineCode">VS1053 sine test frequency code. Default is <c>0x44</c>.</param>
		public void RunStartupSineTest( int durationMs = 3000, byte sineCode = StartupSineFrequency )
		{
			StartSineTest( sineCode );
			Thread.Sleep( durationMs );
			StopSineTest();
		}

		/// <summary>
		/// Starts VS1053 sine test mode.
		/// </summary>
		/// <param name="sineCode">VS1053 sine test frequency code.</param>
		public void StartSineTest( byte sineCode = StartupSineFrequency )
		{
			ushort mode = ( ushort )( ( Mode.SdiNew | Mode.AllowSdiTests ) & unchecked(( ushort )~Mode.Line1Selector) );
			SciWrite( Register.Mode, mode );

			SdiWriteStrictTestFrame(
			[
				0x53, 0xEF, 0x6E, sineCode,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00
			] );
		}

		/// <summary>
		/// Stops VS1053 sine test mode and restores startup register configuration.
		/// </summary>
		public void StopSineTest()
		{
			SdiWriteStrictTestFrame(
			[
				0x45, 0x78, 0x69, 0x74,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00
			] );

			SciWrite( Register.Mode, Mode.SdiNew );
			Thread.Sleep( 20 );
			ConfigureStartupRegisters();
			Thread.Sleep( 50 );
		}

		/// <summary>
		/// Plays an audio file by determining its format and streaming it to the VS1053 decoder.
		/// </summary>
		/// <param name="filePath">Absolute or relative path to the source media file.</param>
		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="NotSupportedException"></exception>
		public void PlaySong( string filePath )
		{
			if ( string.IsNullOrEmpty( filePath ) )
			{
				throw new ArgumentException( "File path cannot be null or empty.", nameof( filePath ) );
			}

			if ( !File.Exists( filePath ) )
			{
				throw new ArgumentException( $"File not found: {filePath}" );
			}

			FileInfo fileInfo = new( filePath );

			_ = fileInfo.Extension.ToLower() switch
			{
				".mp3"  => PlayFileCore( filePath, "MP3",   2052 ),
				".wav"  => PlayFileCore( filePath, "WAV",   2052 ),
				".flac" => PlayFileCore( filePath, "FLAC", 12288 ),
				".ogg"  => PlayFileCore( filePath, "OGG",   2052 ),
				".aac"  => PlayFileCore( filePath, "AAC",   2052 ),
				".m4a"  => PlayFileCore( filePath, "M4A",   2052 ),
				".dsd"  => PlayFileCore( filePath, "DSD",  12288 ),
				".dsf"  => PlayFileCore( filePath, "DSF",  12288 ),
				".dff"  => PlayFileCore( filePath, "DFF",  12288 ),
				_ => throw new NotSupportedException( $"Unsupported audio format: {fileInfo.Extension}" )
			};

			Thread.Sleep( 1000 );
		}

		private void LogPlaybackState( string label, string stage )
		{
			ushort sciMode = SciRead( Register.Mode );
			ushort sciStatus = SciRead( Register.Status );
			ushort sciClockf = SciRead( Register.ClockFrequency );
			ushort sciAudata = SciRead( Register.AudioData );
			ushort sciHdat0 = SciRead( Register.StreamHeaderData0 );
			ushort sciHdat1 = SciRead( Register.StreamHeaderData1 );
			string dreq = this.dreqPin.Read() == GpioPinValue.High ? "HIGH" : "LOW";

			Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] {label}: {stage}: DREQ={dreq}, SCI_MODE={sciMode:X4}, SCI_STATUS={sciStatus:X4}, SCI_CLOCKF={sciClockf:X4}, SCI_AUDATA={sciAudata:X4}, SCI_HDAT0={sciHdat0:X4}, SCI_HDAT1={sciHdat1:X4}" );
		}

		/// <summary>
		/// Opens an audio file and streams its payload to the VS1053 decoder over SDI.
		/// </summary>
		/// <param name="filePath">Absolute or relative path to the source media file.</param>
		/// <param name="label">
		/// Media discriminator used by this method to select format-specific preprocessing.
		/// Supported values in current callers are <c>"MP3"</c>, <c>"WAV"</c>, <c>"FLAC"</c>, <c>"OGG"</c>, <c>"AAC"</c>, <c>"M4A"</c>, and <c>"DSD"</c>.
		/// </param>
		/// <param name="fillerBytes">Number of filler bytes to send after file completion for decoder flush.</param>
		/// <exception cref="InvalidOperationException">
		/// Thrown when the computed media payload start offset is outside the file bounds.
		/// </exception>
		/// <remarks>
		/// For MP3 input, ID3/tag data is skipped via <see cref="FindMp3DataStart(FileStream)"/>.
		/// After payload transmission, decoder flush bytes are sent to let the codec finish decoding buffered frames.
		/// Different formats require different numbers of filler bytes (MP3/WAV/OGG/AAC/M4A: 2052, FLAC/DSD: 12288).
		/// </remarks>
		private bool PlayFileCore( string filePath, string label, int fillerBytes )
		{
			this.SoftReset();

			try
			{
				using var fs = new FileStream( filePath, FileMode.Open, FileAccess.Read );

				long dataStart = 0;
				long totalFileBytes = fs.Length;
				Mp4AacTrackInfo m4aTrackInfo = null;

				if( label == "MP3" )
				{
					dataStart = FindMp3DataStart( fs );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] MP3 data start: {dataStart}" );
				}
				else if( label == "WAV" )
				{
					// For WAV, keep the RIFF headers so decoder recognizes the format
					// The decoder needs the RIFF structure to auto-detect PCM
					dataStart = 0;  // Include RIFF header for format detection

					// But limit the file size to just the audio data (no trailing garbage)
					long actualAudioStart = FindWavDataStart( fs );  // Get real offset (usually 44)
					long dataChunkDataSize = FindWavDataChunkSize( fs );
					totalFileBytes = actualAudioStart + dataChunkDataSize;  // RIFF + fmt + data-header + audio

					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] WAV: streaming from offset 0 (with RIFF headers) up to byte {totalFileBytes}" );
				}
				else if( label == "FLAC" )
				{
					this.patchEngine.LoadPlugin( patchType: PatchEngine.PatchType.Flac );

					// FLAC files start with "fLaC" (0x66 0x4C 0x61 0x43)
					// No metadata skipping needed; send entire file from beginning
					dataStart = 0;
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] FLAC: starting from beginning (fLaC header)" );
				}
				else if( label == "M4A" )
				{
					this.patchEngine.LoadPlugin( patchType: PatchEngine.PatchType.StandardCodec );

					dataStart = 0;
					totalFileBytes = fs.Length;

					LogMp4ContainerInfo( fs );

					if( TryBuildMp4AacTrackInfo( fs, out m4aTrackInfo ) )
					{
						Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] M4A: AAC track detected (samples={m4aTrackInfo.SampleSizes.Length}, sampleRate={m4aTrackInfo.SampleRate}, channels={m4aTrackInfo.ChannelCount})" );
					}
					else
					{
						Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A warning: AAC track parsing failed; streaming container as-is." );
					}
				}
				else if( label == "DSD" )
				{
					this.patchEngine.LoadPlugin( patchType: PatchEngine.PatchType.Dsd );

					dataStart = 0;
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD: starting from beginning (container header included)" );
					LogDsdContainerInfo( fs );
				}

				if( dataStart >= fs.Length )
				{
					throw new InvalidOperationException( "Invalid media data start position." );
				}

				fs.Position = dataStart;
				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] {label}: Total bytes to send={totalFileBytes}, starting at offset={dataStart}, fillers={fillerBytes}" );

				// Patch-based formats keep their patch-provided runtime context.
				if( label != "FLAC" && label != "DSD" )
				{
					ConfigureStartupRegisters();
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Startup registers configured" );
				}
				else
				{
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] {label}: Skipping ConfigureStartupRegisters (patch context active)" );
				}

				if( label == "M4A" )
				{
					LogPlaybackState( label, "pre-start" );
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A: verifying MP4 container signature before streaming" );
				}

				int targetSdiClock = label == "DSD" ? DsdDataSPIFrequency : DataSPIFrequency;
				this.spiDataDevice.ConnectionSettings.ClockFrequency = targetSdiClock;
				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] {label}: SDI clock set to {targetSdiClock} Hz" );

				Thread.Sleep( 20 );

				if( label == "DSD" )
				{
					SciWrite( Register.ClockFrequency, DsdClockf );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] DSD: SCI_CLOCKF set to 0x{DsdClockf:X4}" );
					Thread.Sleep( 2 );
				}

				// Adjust volume for WAV - not too low, not too high to avoid distortion
				if( label == "WAV" )
				{
					SetVolume( 250, 250 );  // Moderate volume for WAV
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] WAV: Volume set to 250" );

					// Dump SCI registers for diagnostics
					ushort sciMode = SciRead( Register.Mode );
					ushort sciStatus = SciRead( Register.Status );
					ushort sciAudata = SciRead( Register.AudioData );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] WAV: SCI_MODE={sciMode:X4}, SCI_STATUS={sciStatus:X4}, SCI_AUDATA={sciAudata:X4}" );
				}
				else if( label == "FLAC" )
				{
					SetVolume( 250, 250 );  // Same volume for FLAC
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] FLAC: Volume set to 250" );

					// Dump SCI registers for diagnostics AFTER patching
					ushort sciMode = SciRead( Register.Mode );
					ushort sciStatus = SciRead( Register.Status );
					ushort sciAudata = SciRead( Register.AudioData );
					ushort sciWramAddr = SciRead( Register.WRAMBaseAddress );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] FLAC: SCI_MODE={sciMode:X4}, SCI_STATUS={sciStatus:X4}, SCI_AUDATA={sciAudata:X4}, WRAMADDR={sciWramAddr:X4}" );
				}

				bool applyStartupMode = label != "FLAC" && label != "DSD";
				byte startFillByte = label == "DSD" ? DsdEndFillByte : EndFillByte;

				StartSong( applyStartupMode, startFillByte );

				if( label == "M4A" )
				{
					LogPlaybackState( label, "post-start" );
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A: MP4 container handoff complete" );
				}

				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Song started, streaming {label} data..." );

				long totalBytesStreamed = 0;
				bool dsdDetectionChecked = label != "DSD";

				if( label == "M4A" && m4aTrackInfo != null )
				{
					DateTime m4aStreamStart = DateTime.UtcNow;
					if( TryStreamMp4AacAsAdts( fs, m4aTrackInfo, out totalBytesStreamed ) )
					{
						TimeSpan elapsed = DateTime.UtcNow - m4aStreamStart;
						double elapsedSeconds = Math.Max( elapsed.TotalSeconds, 0.001 );
						double bytesPerSecond = totalBytesStreamed / elapsedSeconds;
						Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] M4A ADTS stream phase: {totalBytesStreamed} bytes in {(int)elapsed.TotalMilliseconds} ms ({bytesPerSecond:F0} B/s)" );
					}
					else
					{
						Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A warning: AAC track streaming failed; falling back to raw container stream." );
						totalBytesStreamed = 0;
					}
				}

				if( totalBytesStreamed == 0 )
				{
					int streamChunkSize = label == "DSD" ? 8192 : 4096;
					var chunk = new byte[ streamChunkSize ];
					int read;
					long bytesRemaining = totalFileBytes - dataStart;

					while( bytesRemaining > 0 && ( read = fs.Read( chunk, 0, ( int )Math.Min( chunk.Length, bytesRemaining ) ) ) > 0 )
					{
						byte[] toWrite = chunk;
						if( read < chunk.Length )
						{
							// Only resize if partial chunk at end
							toWrite = new byte[ read ];
							Array.Copy( chunk, 0, toWrite, 0, read );
						}
						totalBytesStreamed += read;
						bytesRemaining -= read;

						SdiWriteChunks( toWrite );

						if( !dsdDetectionChecked && totalBytesStreamed >= 131072 )
						{
							ushort liveHeaderData1 = SciRead( Register.StreamHeaderData1 );
							Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] DSD live check: SCI_HDAT1=0x{liveHeaderData1:X4}" );
							if( !IsDsdDetected( liveHeaderData1 ) )
							{
								Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD live check warning: decoder has not reported 'DS'." );
							}

							dsdDetectionChecked = true;
						}
					}
				}

				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Streamed {totalBytesStreamed} bytes for {label}" );
				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Sending {fillerBytes} filler bytes to {label} decoder..." );

				if( label == "DSD" )
				{
					SdiSendFillers( fillerBytes, DsdEndFillByte );
				}
				else
				{
					SdiSendFillers( fillerBytes );
				}

				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Filler bytes sent, decoder flushing..." );
				Thread.Sleep( 100 );

				ushort headerData0 = SciRead( Register.StreamHeaderData0 );
				ushort headerData1 = SciRead( Register.StreamHeaderData1 );
				ushort decodeTime = SciRead( Register.DecodeTime );

				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] Final state: HeaderData0={headerData0:X4}, HeaderData1={headerData1:X4}, DecodeTime={decodeTime}" );
				if( label == "M4A" )
				{
					LogPlaybackState( label, "final" );
				}

				if( label == "DSD" && !IsDsdDetected( headerData1 ) )
				{
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] DSD warning: SCI_HDAT1 is 0x{headerData1:X4} (expected 'DS' marker)." );
				}

				// For WAV, dump all SCI registers to see error status
				if( label == "WAV" )
				{
					ushort sciMode = SciRead( Register.Mode );
					ushort sciStatus = SciRead( Register.Status );
					ushort sciAudata = SciRead( Register.AudioData );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] WAV Final: SCI_MODE={sciMode:X4}, SCI_STATUS={sciStatus:X4}, SCI_AUDATA={sciAudata:X4}" );
				}
				else if( label == "FLAC" )
				{
					ushort sciMode = SciRead( Register.Mode );
					ushort sciStatus = SciRead( Register.Status );
					ushort sciAudata = SciRead( Register.AudioData );
					ushort sciWramAddr = SciRead( Register.WRAMBaseAddress );

					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] FLAC Final: SCI_MODE={sciMode:X4}, SCI_STATUS={sciStatus:X4}, SCI_AUDATA={sciAudata:X4}, WRAMADDR={sciWramAddr:X4}" );
				}

				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] {label} playback complete" );

				// Note: No cleanup SoftReset here anymore - it would destroy loaded patches!
				// If needed, let the caller handle cleanup between different formats.
			}
			catch( Exception ex )
			{
				Debug.WriteLineIf( EnableVerboseTrace, $"Error during {label} playback: {ex.Message}" );
				return false;
			}

			return true;
		}

		/// <summary>
		/// Prepares the decoder pipeline and sends initial filler bytes before audio streaming.
		/// </summary>
		/// <param name="applyStartupMode">If true, rewrites SCI_MODE to the generic startup mode before playback.</param>
		/// <param name="fillByte">Fill byte used for initial SDI priming.</param>
		public void StartSong( bool applyStartupMode = true, byte fillByte = EndFillByte )
		{
			if( applyStartupMode )
			{
				// Ensure that the mode for baseline playback is set correctly (Line Out, not Microphone!)
				ushort mode = ( ushort )( this.startupMode & unchecked(( ushort )~Mode.Line1Selector) );
				SciWrite( Register.Mode, mode );
				Thread.Sleep( 10 );
			}

			// Enable amplifier through analog path
			this.EnableAnalogPath();
			Thread.Sleep( 10 );

			// Send 10 filler bytes to prepare FIFO
			SdiSendFillers( 10, fillByte );
		}

		/// <summary>
		/// Requests graceful decoding cancellation and drains the VS1053 input buffer.
		/// </summary>
		/// <remarks>
		/// Implements the VS10xx-recommended SM_CANCEL sequence with filler bytes and polling.
		/// </remarks>
		public void StopSong()
		{
			// Ensure that the mode is set correctly (Line Out, not Microphone!)
			ushort mode = SciRead( Register.Mode );
			mode = ( ushort )( mode | Mode.CancelDecodingCurrentFile | Mode.SdiNew );
			mode &= unchecked(( ushort )~Mode.Line1Selector);  // Line1Selector MUST be 0!
			SciWrite( Register.Mode, mode );
			Thread.Sleep( 10 );

			// Send 2052 filler bytes before stopping
			SdiSendFillers( 2052 );

			// Poll until SM_CANCEL is cleared by the chip (max 20 iterations x 10ms)
			for( int i = 0; i < 20; i++ )
			{
				SdiSendFillers( 32 );

				ushort modeReg = SciRead( Register.Mode );

				if( ( modeReg & Mode.CancelDecodingCurrentFile ) == 0 )
				{
					// SM_CANCEL was cleared, song stopped correctly
					SdiSendFillers( 2052 );
					Thread.Sleep( 10 );
					return;
				}

				Thread.Sleep( 10 );
			}

		}

		/// <summary>
		/// Issues a software reset via SCI_MODE and waits for DREQ readiness.
		/// </summary>
		public void SoftReset()
		{
			SciWrite( Register.Mode, Mode.SdiNew | Mode.SoftReset );
			Thread.Sleep( 10 );
			AwaitDataRequest( 500 );
		}

		/// <summary>
		/// Sends raw audio data to SDI in codec-sized chunks.
		/// </summary>
		/// <param name="data">Input audio payload.</param>
		/// <remarks>
		/// If an ID3v2 tag is detected at the beginning, it is skipped automatically.
		/// </remarks>
		public void SdiWriteChunks( byte[] data )
		{
			if( data == null )
			{
				return;
			}

			SdiWriteChunks( data, 0, data.Length );
		}

		/// <summary>
		/// Sends a segment of audio data to SDI in codec-sized chunks.
		/// </summary>
		/// <param name="data">Input audio payload.</param>
		/// <param name="offset">Start offset within <paramref name="data"/>.</param>
		/// <param name="count">Number of bytes to send.</param>
		public void SdiWriteChunks( byte[] data, int offset, int count )
		{
			SdiWriteChunks( data, offset, count, ChunkSize, true );
		}

		private void SdiWriteChunks( byte[] data, int offset, int count, int writeChunkSize )
		{
			SdiWriteChunks( data, offset, count, writeChunkSize, false );
		}

		private void SdiWriteChunks( byte[] data, int offset, int count, int writeChunkSize, bool allowId3Skip )
		{
			if( data == null || count <= 0 || offset < 0 || offset >= data.Length )
			{
				return;
			}

			if( offset + count > data.Length )
			{
				count = data.Length - offset;
			}

			if( count <= 0 )
			{
				return;
			}

			if( allowId3Skip && offset == 0 && count > 10 && data[ 0 ] == 'I' && data[ 1 ] == 'D' && data[ 2 ] == '3' )
			{
				int tagSize = ( data[ 6 ] << 21 ) | ( data[ 7 ] << 14 ) | ( data[ 8 ] << 7 ) | data[ 9 ];
				offset = 10 + tagSize;
				count -= offset;
			}

			if( count <= 0 )
			{
				return;
			}

			int effectiveChunkSize = writeChunkSize <= 0 ? ChunkSize : writeChunkSize;

			lock( this.spiSync )
			{
				int remaining = count;
				int pos = offset;

				EnableSdi();

				try
				{
					while( remaining > 0 )
					{
						AwaitDataRequest( 1500 );
						int chunkSize = Math.Min( effectiveChunkSize, remaining );
						this.spiDataDevice.Write( data, pos, chunkSize );
						pos += chunkSize;
						remaining -= chunkSize;
					}
				}
				finally
				{
					DisableSdi();
				}
			}
		}

		/// <summary>
		/// Sends a specified number of filler bytes to the VS1053 SDI channel.
		/// </summary>
		/// <param name="count">The number of filler bytes to send.</param>
		private void SdiSendFillers( int count )
		{
			SdiSendFillers( count, EndFillByte );
		}

		/// <summary>
		/// Sends a specified number of filler bytes to the VS1053 SDI channel using a custom fill byte.
		/// </summary>
		/// <param name="count">The number of filler bytes to send.</param>
		/// <param name="fillByte">Fill byte value to transmit.</param>
		private void SdiSendFillers( int count, byte fillByte )
		{
			if( count <= 0 )
			{
				return;
			}

			for( int i = 0; i < ChunkSize; i++ )
			{
				this.dataBlock[ i ] = fillByte;
			}

			lock( this.spiSync )
			{
				int remaining = count;
				while( remaining > 0 )
				{
					AwaitDataRequest( 1500 );

					int chunkSize = Math.Min( ChunkSize, remaining );

					EnableSdi();

					try
					{
						this.spiDataDevice.Write( this.dataBlock, 0, chunkSize );
					}
					finally
					{
						DisableSdi();
					}
					remaining -= chunkSize;
				}
			}
		}

		/// <summary>
		/// Waits until DREQ is high or throws on timeout.
		/// </summary>
		/// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
		/// <exception cref="TimeoutException">Thrown if DREQ does not become high in time.</exception>
		public void AwaitDataRequest( int timeoutMs = 1000 )
		{
			if( this.dreqPin.Read() == GpioPinValue.High )
			{
				return;
			}

			var startTime = DateTime.UtcNow;
			int spinCount = 0;

			while( this.dreqPin.Read() == GpioPinValue.Low )
			{
				if( DreqPollIntervalMs > 0 )
				{
					Thread.Sleep( DreqPollIntervalMs );
				}

				spinCount++;
				if( ( spinCount & 0x1F ) == 0 )
				{
					var elapsedMs = ( int )( DateTime.UtcNow - startTime ).TotalMilliseconds;
					if( elapsedMs >= timeoutMs )
					{
						throw new TimeoutException( "VS1053 DREQ timeout." );
					}
				}
			}
		}

		/// <summary>
		/// Waits for patch-triggered restart completion by synchronizing on DREQ state.
		/// </summary>
		/// <param name="lowDetectWindowMs">Time window to observe an expected DREQ low pulse.</param>
		/// <param name="readyTimeoutMs">Maximum time to wait for stable DREQ high readiness.</param>
		public void AwaitPatchReady( int lowDetectWindowMs = 300, int readyTimeoutMs = 2000 )
		{
			int waited = 0;

			while( waited < lowDetectWindowMs && this.dreqPin.Read() == GpioPinValue.High )
			{
				Thread.Sleep( 1 );
				waited++;
			}

			WaitDreqStableHigh( 3, readyTimeoutMs );
		}

		/// <summary>
		/// Writes baseline SCI register configuration used after reset and recovery paths.
		/// </summary>
		/// <remarks>
		/// Also clears the decoded-header status bit and enables analog output stage.
		/// </remarks>
		public void ConfigureStartupRegisters()
		{
			ushort mode = this.startupMode;
			mode &= unchecked(( ushort )~Mode.SoftReset);
			mode &= unchecked(( ushort )~Mode.Line1Selector);
			SciWrite( Register.Mode, mode );
			SciWrite( Register.ClockFrequency, RecommendedClockf );
			SciWrite( Register.BassTreble, 0x0000 );

			// Clear SS_DO_NOT_JUMP (bit 15 in SCI_STATUS) per VS10xx note after reset/cancel paths.
			ushort status = SciRead( Register.Status );
			status &= unchecked(( ushort )~Status.DecodedHeader);
			SciWrite( Register.Status, status );

			EnableAnalogPath();
			SetVolume( 250, 250 );
		}

		/// <summary>
		/// Performs a hardware reset of the VS1053 and waits for DREQ to become high.
		/// </summary>
		public void ResetHardware()
		{
			DisableSci();
			DisableSdi();

			this.resetPin.Write( GpioPinValue.Low );
			Thread.Sleep( 5 );
			this.resetPin.Write( GpioPinValue.High );
			Thread.Sleep( 5 );

			PrimeSpi();
			WaitDreqStableHigh( 10, 2000 );
		}

		/// <summary>
		/// Clears analog power-down bits in SCI_STATUS to enable the analog output path.
		/// </summary>
		public void EnableAnalogPath()
		{
			ushort status = SciRead( Register.Status );
			status &= unchecked(( ushort )~( Status.AnalogDriverPowerdown | Status.AnalogInternalPowerdown ));
			SciWrite( Register.Status, status );
			Thread.Sleep( 2 );
		}

		/// <summary>
		/// Writes a 16-bit value to a VS1053 SCI register at the specified address.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <param name="data">The 16-bit value to write.</param>
		private void SciWrite( Register register, ushort data )
		{
			AwaitDataRequest( 1500 );

			this.cmdBuffer[ 0 ] = 0x02;
			this.cmdBuffer[ 1 ] = ( byte )register;
			this.cmdBuffer[ 2 ] = ( byte )( data >> 8 );
			this.cmdBuffer[ 3 ] = ( byte )data;

			lock( this.spiSync )
			{
				EnableSci();

				try
				{
					this.spiCmdDevice.Write( this.cmdBuffer );
				}
				finally
				{
					DisableSci();
				}
			}

			AwaitDataRequest( 1500 );
		}

		/// <summary>
		/// Writes a 16-bit value to a VS1053 SCI register at the specified address, with a single retry on timeout.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <param name="data">The 16-bit value to write.</param>
		private void SciWriteWithRecovery( Register register, ushort data )
		{
			for( int attempt = 0; attempt < 2; attempt++ )
			{
				try
				{
					SciWrite( register, data );
					return;
				}
				catch( TimeoutException )
				{
					if( attempt == 1 )
					{
						throw;
					}

					ResetHardware();
					ConfigureStartupRegisters();
					WaitDreqStableHigh( 3, 1000 );
					Thread.Sleep( 20 );
				}
			}
		}

		/// <summary>
		/// Reads a 16-bit value from a VS1053 SCI register at the specified address.
		/// </summary>
		/// <param name="address">The address of the SCI register.</param>
		/// <returns>The 16-bit value read from the SCI register.</returns>
		private ushort SciRead( Register register )
		{
			AwaitDataRequest( 1500 );

			this.cmdBuffer[ 0 ] = 0x03;
			this.cmdBuffer[ 1 ] = ( byte )register;
			this.cmdBuffer[ 2 ] = 0xFF;
			this.cmdBuffer[ 3 ] = 0xFF;

			var read = new byte[ 4 ];

			lock( this.spiSync )
			{
				EnableSci();

				try
				{
					this.spiCmdDevice.TransferFullDuplex( this.cmdBuffer, 0, this.cmdBuffer.Length, read, 0, read.Length );
				}
				finally
				{
					DisableSci();
				}
			}

			AwaitDataRequest( 1500 );

			return ( ushort )( ( read[ 2 ] << 8 ) | read[ 3 ] );
		}

		/// <summary>
		/// Writes a byte array to the VS1053 SDI channel in chunks, waiting for DREQ readiness.	
		/// </summary>
		/// <param name="data">The byte array to write.</param>
		private void SdiWrite( byte[] data )
		{
			SdiWrite( data, 0, data.Length );
		}

		/// <summary>
		/// Writes a portion of a byte array to the VS1053 SDI channel in chunks, waiting for DREQ readiness.
		/// </summary>
		/// <param name="data">The byte array to write.</param>
		/// <param name="offset">The zero-based byte offset in the array at which to begin writing bytes.</param>
		/// <param name="count">The number of bytes to write.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when the offset or count is out of range.</exception>
		private void SdiWrite( byte[] data, int offset, int count )
		{
			if( count <= 0 )
			{
				return;
			}

			if( offset < 0 || count < 0 || offset + count > data.Length )
			{
				throw new ArgumentOutOfRangeException();
			}

			lock( this.spiSync )
			{
				int remaining = count;
				int position = offset;

				EnableSdi();

				try
				{
					while( remaining > 0 )
					{
						AwaitDataRequest( 1500 );

						int chunk = remaining > ChunkSize ? ChunkSize : remaining;
						Array.Copy( data, position, this.dataBlock, 0, chunk );

						this.spiDataDevice.Write( this.dataBlock, 0, chunk );

						position += chunk;
						remaining -= chunk;
					}
				}
				finally
				{
					DisableSdi();
				}
			}
		}

		/// <summary>
		/// Writes a strict test frame to the VS1053 SDI channel, one byte at a time, waiting for DREQ readiness before each byte.
		/// </summary>
		/// <param name="frame">The byte array representing the strict test frame to write.</param>
		/// <exception cref="ArgumentNullException">Thrown when the frame is null.</exception>
		private void SdiWriteStrictTestFrame( byte[] frame )
		{
			if( frame == null )
			{
				throw new ArgumentNullException( nameof( frame ) );
			}

			AwaitDataRequest( 500 );

			lock( this.spiSync )
			{
				EnableSdi();

				try
				{
					for( int index = 0; index < frame.Length; index++ )
					{
						this.singleByteBuffer[ 0 ] = frame[ index ];
						this.spiDataDevice.Write( this.singleByteBuffer );
					}
				}
				finally
				{
					DisableSdi();
				}
			}
		}

		/// <summary>
		/// Logs container-level diagnostics for DSD inputs.
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		private static void LogDsdContainerInfo( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				if( fs.Length < 64 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD: file too small for container diagnostics." );
					return;
				}

				var header = new byte[ 96 ];
				fs.Position = 0;
				int got = fs.Read( header, 0, header.Length );
				if( got < 64 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD: unable to read container header." );
					return;
				}

				if( header[ 0 ] == ( byte )'D' && header[ 1 ] == ( byte )'S' && header[ 2 ] == ( byte )'D' && header[ 3 ] == ( byte )' ' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD container detected: DSF" );

					if( header[ 28 ] == ( byte )'f' && header[ 29 ] == ( byte )'m' && header[ 30 ] == ( byte )'t' && header[ 31 ] == ( byte )' ' )
					{
						uint channels = ReadUInt32LittleEndian( header, 52 );
						uint sampleRate = ReadUInt32LittleEndian( header, 56 );
						uint bitsPerSample = ReadUInt32LittleEndian( header, 60 );
						Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] DSD DSF fmt: Channels={channels}, SampleRate={sampleRate}, BitsPerSample={bitsPerSample}" );

						if( channels != 2 || sampleRate != 2822400 )
						{
							Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD warning: VS1053 DSD patch expects stereo DSD64 (2 channels, 2822400 Hz)." );
						}
					}
				}
				else if( header[ 0 ] == ( byte )'F' && header[ 1 ] == ( byte )'R' && header[ 2 ] == ( byte )'M' && header[ 3 ] == ( byte )'8' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD container detected: DFF" );
				}
				else
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] DSD warning: container signature is neither DSF nor DFF." );
				}
			}
			finally
			{
				fs.Position = original;
			}
		}

		private static bool IsDsdDetected( ushort headerData1 )
		{
			return headerData1 == 0x4453 || headerData1 == 0x5344 || headerData1 == 0x4444;
		}

		private static uint ReadUInt32LittleEndian( byte[] buffer, int offset )
		{
			return ( uint )( buffer[ offset ]
				| ( buffer[ offset + 1 ] << 8 )
				| ( buffer[ offset + 2 ] << 16 )
				| ( buffer[ offset + 3 ] << 24 ) );
		}

		private static uint ReadUInt32BigEndian( byte[] buffer, int offset )
		{
			return ( uint )( ( buffer[ offset ] << 24 )
				| ( buffer[ offset + 1 ] << 16 )
				| ( buffer[ offset + 2 ] << 8 )
				| buffer[ offset + 3 ] );
		}

		private static ulong ReadUInt64BigEndian( byte[] buffer, int offset )
		{
			return ( ( ulong )buffer[ offset ] << 56 )
				| ( ( ulong )buffer[ offset + 1 ] << 48 )
				| ( ( ulong )buffer[ offset + 2 ] << 40 )
				| ( ( ulong )buffer[ offset + 3 ] << 32 )
				| ( ( ulong )buffer[ offset + 4 ] << 24 )
				| ( ( ulong )buffer[ offset + 5 ] << 16 )
				| ( ( ulong )buffer[ offset + 6 ] << 8 )
				| buffer[ offset + 7 ];
		}

		/// <summary>
		/// Scans the beginning of an MP3 stream and returns the best data start offset.
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		/// <returns>Byte position of first MP3 frame sync or derived fallback offset.</returns>
		/// <remarks>
		/// The original stream position is restored before returning.
		/// </remarks>
		private static long FindMp3DataStart( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				int scanLen = ( int )Math.Min( 64 * 1024, fs.Length );

				if( scanLen < 2 )
				{
					return 0;
				}

				fs.Position = 0;

				var probe = new byte[ scanLen ];
				int got = fs.Read( probe, 0, probe.Length );

				if( got < 2 )
				{
					return 0;
				}

				// Prefer the earliest MP3 frame sync. Metadata before that is skipped implicitly.
				for( int i = 0; i <= got - 2; i++ )
				{
					if( probe[ i ] == 0xFF && ( probe[ i + 1 ] & 0xE0 ) == 0xE0 )
					{
						return i;
					}
				}

				// Fallback: ID3 signature (if present in this stream layout)
				for( int i = 0; i <= got - 10; i++ )
				{
					if( probe[ i ] == ( byte )'I' && probe[ i + 1 ] == ( byte )'D' && probe[ i + 2 ] == ( byte )'3' )
					{
						int size = ( probe[ i + 6 ] << 21 ) | ( probe[ i + 7 ] << 14 ) | ( probe[ i + 8 ] << 7 ) | probe[ i + 9 ];
						long pos = i + 10 + size;
						if( pos < fs.Length )
						{
							return pos;
						}
					}
				}

				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		private struct StscEntry
		{
			public uint FirstChunk;
			public uint SamplesPerChunk;
			public uint SampleDescriptionIndex;
		}

		private sealed class Mp4AacTrackInfo
		{
			public int SampleRate;
			public int ChannelCount;
			public int AacProfile = 2;
			public uint[] SampleSizes = new uint[ 0 ];
			public long[] ChunkOffsets = new long[ 0 ];
			public StscEntry[] SampleToChunk = new StscEntry[ 0 ];
		}

		private static ushort ReadUInt16BigEndian( byte[] buffer, int offset )
		{
			return ( ushort )( ( buffer[ offset ] << 8 ) | buffer[ offset + 1 ] );
		}

		private static bool FindAtomInRange( FileStream fs, long rangeStart, long rangeEnd, string targetType, out long atomStart, out long payloadStart, out long atomEnd )
		{
			atomStart = 0;
			payloadStart = 0;
			atomEnd = 0;

			long pos = rangeStart;
			while( pos + 8 <= rangeEnd )
			{
				fs.Position = pos;
				var header = new byte[ 16 ];
				int got = fs.Read( header, 0, header.Length );
				if( got < 8 )
				{
					return false;
				}

				uint size32 = ReadUInt32BigEndian( header, 0 );
				string atomType = new string( new[] { ( char )header[ 4 ], ( char )header[ 5 ], ( char )header[ 6 ], ( char )header[ 7 ] } );
				long headerSize = 8;
				long atomSize = size32;

				if( size32 == 1 )
				{
					if( got < 16 )
					{
						return false;
					}

					headerSize = 16;
					atomSize = ( long )ReadUInt64BigEndian( header, 8 );
				}
				else if( size32 == 0 )
				{
					atomSize = rangeEnd - pos;
				}

				if( atomSize < headerSize )
				{
					return false;
				}

				long currentAtomEnd = pos + atomSize;
				if( currentAtomEnd > rangeEnd )
				{
					currentAtomEnd = rangeEnd;
				}

				if( atomType == targetType )
				{
					atomStart = pos;
					payloadStart = pos + headerSize;
					atomEnd = currentAtomEnd;
					return true;
				}

				pos += atomSize;
			}

			return false;
		}

		private static bool TryBuildMp4AacTrackInfo( FileStream fs, out Mp4AacTrackInfo trackInfo )
		{
			trackInfo = null;

			long original = fs.Position;
			try
			{
				if( !FindAtomInRange( fs, 0, fs.Length, "moov", out _, out long moovPayloadStart, out long moovAtomEnd ) )
				{
					return false;
				}

				long searchPos = moovPayloadStart;
				while( FindAtomInRange( fs, searchPos, moovAtomEnd, "trak", out _, out long trakPayloadStart, out long trakAtomEnd ) )
				{
					if( TryParseMp4AacTrackInfo( fs, trakPayloadStart, trakAtomEnd, out trackInfo ) )
					{
						return true;
					}

					searchPos = trakAtomEnd;
				}

				return false;
			}
			finally
			{
				fs.Position = original;
			}
		}

		private static bool TryParseMp4AacTrackInfo( FileStream fs, long trakPayloadStart, long trakAtomEnd, out Mp4AacTrackInfo trackInfo )
		{
			trackInfo = null;

			if( !FindAtomInRange( fs, trakPayloadStart, trakAtomEnd, "mdia", out _, out long mdiaPayloadStart, out long mdiaAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, mdiaPayloadStart, mdiaAtomEnd, "hdlr", out _, out long hdlrPayloadStart, out long hdlrAtomEnd ) )
			{
				return false;
			}

			fs.Position = hdlrPayloadStart + 8;
			var handler = new byte[ 4 ];
			if( fs.Read( handler, 0, handler.Length ) < handler.Length )
			{
				return false;
			}

			if( handler[ 0 ] != ( byte )'s' || handler[ 1 ] != ( byte )'o' || handler[ 2 ] != ( byte )'u' || handler[ 3 ] != ( byte )'n' )
			{
				return false;
			}

			if( !FindAtomInRange( fs, mdiaPayloadStart, mdiaAtomEnd, "minf", out _, out long minfPayloadStart, out long minfAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, minfPayloadStart, minfAtomEnd, "stbl", out _, out long stblPayloadStart, out long stblAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsd", out _, out long stsdPayloadStart, out long stsdAtomEnd ) )
			{
				return false;
			}

			fs.Position = stsdPayloadStart;
			var stsdHeader = new byte[ 48 ];
			if( fs.Read( stsdHeader, 0, stsdHeader.Length ) < 44 )
			{
				return false;
			}

			uint entryCount = ReadUInt32BigEndian( stsdHeader, 4 );
			if( entryCount < 1 )
			{
				return false;
			}

			string sampleEntryType = new string( new[] { ( char )stsdHeader[ 12 ], ( char )stsdHeader[ 13 ], ( char )stsdHeader[ 14 ], ( char )stsdHeader[ 15 ] } );
			if( sampleEntryType != "mp4a" )
			{
				return false;
			}

			var track = new Mp4AacTrackInfo();
			track.ChannelCount = ReadUInt16BigEndian( stsdHeader, 32 );
			track.SampleRate = ( int )( ReadUInt32BigEndian( stsdHeader, 40 ) >> 16 );

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsz", out _, out long stszPayloadStart, out _ ) )
			{
				return false;
			}

			fs.Position = stszPayloadStart;
			var stszHeader = new byte[ 12 ];
			if( fs.Read( stszHeader, 0, stszHeader.Length ) < stszHeader.Length )
			{
				return false;
			}

			uint constantSampleSize = ReadUInt32BigEndian( stszHeader, 4 );
			uint sampleCount = ReadUInt32BigEndian( stszHeader, 8 );
			if( sampleCount == 0 )
			{
				return false;
			}

			track.SampleSizes = new uint[ sampleCount ];
			if( constantSampleSize != 0 )
			{
				for( int i = 0; i < track.SampleSizes.Length; i++ )
				{
					track.SampleSizes[ i ] = constantSampleSize;
				}
			}
			else
			{
				var sizeBuffer = new byte[ 4 ];
				for( int i = 0; i < track.SampleSizes.Length; i++ )
				{
					if( fs.Read( sizeBuffer, 0, sizeBuffer.Length ) < sizeBuffer.Length )
					{
						return false;
					}

					track.SampleSizes[ i ] = ReadUInt32BigEndian( sizeBuffer, 0 );
				}
			}

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsc", out _, out long stscPayloadStart, out _ ) )
			{
				return false;
			}

			fs.Position = stscPayloadStart;
			var stscHeader = new byte[ 8 ];
			if( fs.Read( stscHeader, 0, stscHeader.Length ) < stscHeader.Length )
			{
				return false;
			}

			uint stscEntryCount = ReadUInt32BigEndian( stscHeader, 4 );
			if( stscEntryCount == 0 )
			{
				return false;
			}

			track.SampleToChunk = new StscEntry[ stscEntryCount ];
			var stscEntryBuffer = new byte[ 12 ];
			for( int i = 0; i < track.SampleToChunk.Length; i++ )
			{
				if( fs.Read( stscEntryBuffer, 0, stscEntryBuffer.Length ) < stscEntryBuffer.Length )
				{
					return false;
				}

				track.SampleToChunk[ i ] = new StscEntry
				{
					FirstChunk = ReadUInt32BigEndian( stscEntryBuffer, 0 ),
					SamplesPerChunk = ReadUInt32BigEndian( stscEntryBuffer, 4 ),
					SampleDescriptionIndex = ReadUInt32BigEndian( stscEntryBuffer, 8 )
				};
			}

			if( FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stco", out _, out long stcoPayloadStart, out _ ) )
			{
				fs.Position = stcoPayloadStart;
				var stcoHeader = new byte[ 8 ];
				if( fs.Read( stcoHeader, 0, stcoHeader.Length ) < stcoHeader.Length )
				{
					return false;
				}

				uint chunkCount = ReadUInt32BigEndian( stcoHeader, 4 );
				if( chunkCount == 0 )
				{
					return false;
				}

				track.ChunkOffsets = new long[ chunkCount ];
				var offsetBuffer = new byte[ 4 ];
				for( int i = 0; i < track.ChunkOffsets.Length; i++ )
				{
					if( fs.Read( offsetBuffer, 0, offsetBuffer.Length ) < offsetBuffer.Length )
					{
						return false;
					}

					track.ChunkOffsets[ i ] = ReadUInt32BigEndian( offsetBuffer, 0 );
				}
			}
			else if( FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "co64", out _, out long co64PayloadStart, out _ ) )
			{
				fs.Position = co64PayloadStart;
				var co64Header = new byte[ 8 ];
				if( fs.Read( co64Header, 0, co64Header.Length ) < co64Header.Length )
				{
					return false;
				}

				uint chunkCount = ReadUInt32BigEndian( co64Header, 4 );
				if( chunkCount == 0 )
				{
					return false;
				}

				track.ChunkOffsets = new long[ chunkCount ];
				var offsetBuffer = new byte[ 8 ];
				for( int i = 0; i < track.ChunkOffsets.Length; i++ )
				{
					if( fs.Read( offsetBuffer, 0, offsetBuffer.Length ) < offsetBuffer.Length )
					{
						return false;
					}

					track.ChunkOffsets[ i ] = ( long )ReadUInt64BigEndian( offsetBuffer, 0 );
				}
			}
			else
			{
				return false;
			}

			if( track.ChunkOffsets.Length == 0 || track.SampleSizes.Length == 0 )
			{
				return false;
			}

			trackInfo = track;
			return true;
		}

		private static uint GetSamplesPerChunk( StscEntry[] entries, uint chunkNumber )
		{
			if( entries == null || entries.Length == 0 )
			{
				return 0;
			}

			StscEntry current = entries[ 0 ];
			for( int i = 1; i < entries.Length; i++ )
			{
				if( chunkNumber < entries[ i ].FirstChunk )
				{
					break;
				}

				current = entries[ i ];
			}

			return current.SamplesPerChunk;
		}

		private static bool TryBuildAdtsHeader( byte[] header, int sampleRate, int channelCount, int sampleSize )
		{
			if( header == null || header.Length < 7 )
			{
				return false;
			}

			if( sampleSize < 0 || channelCount < 1 || channelCount > 7 )
			{
				return false;
			}

			int sampleRateIndex = GetAacSampleRateIndex( sampleRate );
			if( sampleRateIndex < 0 )
			{
				return false;
			}

			int profile = 1;
			int frameLength = sampleSize + 7;
			if( frameLength > 0x1FFF )
			{
				return false;
			}

			header[ 0 ] = 0xFF;
			header[ 1 ] = 0xF1;
			header[ 2 ] = ( byte )( ( profile << 6 ) | ( sampleRateIndex << 2 ) | ( ( channelCount >> 2 ) & 0x01 ) );
			header[ 3 ] = ( byte )( ( ( channelCount & 0x03 ) << 6 ) | ( ( frameLength >> 11 ) & 0x03 ) );
			header[ 4 ] = ( byte )( ( frameLength >> 3 ) & 0xFF );
			header[ 5 ] = ( byte )( ( ( frameLength & 0x07 ) << 5 ) | 0x1F );
			header[ 6 ] = 0xFC;
			return true;
		}

		private static int GetAacSampleRateIndex( int sampleRate )
		{
			switch( sampleRate )
			{
				case 96000: return 0;
				case 88200: return 1;
				case 64000: return 2;
				case 48000: return 3;
				case 44100: return 4;
				case 32000: return 5;
				case 24000: return 6;
				case 22050: return 7;
				case 16000: return 8;
				case 12000: return 9;
				case 11025: return 10;
				case 8000: return 11;
				case 7350: return 12;
				default: return -1;
			}
		}

		private bool TryStreamMp4AacAsAdts( FileStream fs, Mp4AacTrackInfo trackInfo, out long payloadBytesWritten )
		{
			payloadBytesWritten = 0;

			if( trackInfo == null || trackInfo.SampleSizes == null || trackInfo.SampleSizes.Length == 0 || trackInfo.ChunkOffsets == null || trackInfo.ChunkOffsets.Length == 0 )
			{
				return false;
			}

			if( trackInfo.SampleRate <= 0 || trackInfo.ChannelCount < 1 || trackInfo.ChannelCount > 7 )
			{
				return false;
			}

			var adtsHeader = new byte[ 7 ];
			var directSampleBuffer = new byte[ 4096 ];
			var frameBuffer = new byte[ 8192 ];
			int frameBufferPos = 0;
			int sampleIndex = 0;

			for( int chunkIndex = 0; chunkIndex < trackInfo.ChunkOffsets.Length && sampleIndex < trackInfo.SampleSizes.Length; chunkIndex++ )
			{
				uint samplesPerChunk = GetSamplesPerChunk( trackInfo.SampleToChunk, ( uint )( chunkIndex + 1 ) );
				if( samplesPerChunk == 0 )
				{
					return false;
				}

				long samplePos = trackInfo.ChunkOffsets[ chunkIndex ];
				for( uint sampleInChunk = 0; sampleInChunk < samplesPerChunk && sampleIndex < trackInfo.SampleSizes.Length; sampleInChunk++ )
				{
					int sampleSize = ( int )trackInfo.SampleSizes[ sampleIndex++ ];
					if( sampleSize <= 0 )
					{
						return false;
					}

					if( !TryBuildAdtsHeader( adtsHeader, trackInfo.SampleRate, trackInfo.ChannelCount, sampleSize ) )
					{
						return false;
					}

					if( sampleSize + adtsHeader.Length > frameBuffer.Length )
					{
						if( frameBufferPos > 0 )
						{
							SdiWriteChunks( frameBuffer, 0, frameBufferPos, AacSdiChunkSize );
							frameBufferPos = 0;
						}

						SdiWriteChunks( adtsHeader, 0, adtsHeader.Length, adtsHeader.Length );

						fs.Position = samplePos;
						int remaining = sampleSize;
						while( remaining > 0 )
						{
							int toRead = Math.Min( directSampleBuffer.Length, remaining );
							int read = fs.Read( directSampleBuffer, 0, toRead );
							if( read <= 0 )
							{
								return false;
							}

							SdiWriteChunks( directSampleBuffer, 0, read, AacSdiChunkSize );
							remaining -= read;
							samplePos += read;
							payloadBytesWritten += read;
						}
					}
					else
					{
						if( frameBufferPos + adtsHeader.Length + sampleSize > frameBuffer.Length )
						{
							if( frameBufferPos > 0 )
							{
								SdiWriteChunks( frameBuffer, 0, frameBufferPos, AacSdiChunkSize );
								frameBufferPos = 0;
							}
						}

						Array.Copy( adtsHeader, 0, frameBuffer, frameBufferPos, adtsHeader.Length );
						frameBufferPos += adtsHeader.Length;

						fs.Position = samplePos;
						int remaining = sampleSize;
						while( remaining > 0 )
						{
							if( frameBufferPos == frameBuffer.Length )
							{
								SdiWriteChunks( frameBuffer, 0, frameBufferPos, AacSdiChunkSize );
								frameBufferPos = 0;
							}

							int toRead = Math.Min( frameBuffer.Length - frameBufferPos, remaining );
							int read = fs.Read( frameBuffer, frameBufferPos, toRead );
							if( read <= 0 )
							{
								return false;
							}

							frameBufferPos += read;
							remaining -= read;
							samplePos += read;
							payloadBytesWritten += read;

							if( frameBufferPos == frameBuffer.Length )
							{
								SdiWriteChunks( frameBuffer, 0, frameBufferPos, AacSdiChunkSize );
								frameBufferPos = 0;
							}
						}
					}
				}
			}

			if( frameBufferPos > 0 )
			{
				SdiWriteChunks( frameBuffer, 0, frameBufferPos, AacSdiChunkSize );
			}

			return sampleIndex == trackInfo.SampleSizes.Length;
		}

		/// <summary>
		/// Scans an MP4/M4A stream for container metadata and logs the visible atoms.
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		private static void LogMp4ContainerInfo( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				if( fs.Length < 16 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A: file too small for MP4 container diagnostics." );
					return;
				}

				fs.Position = 0;
				var header = new byte[ 16 ];
				int got = fs.Read( header, 0, header.Length );
				if( got < 8 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A: unable to read MP4 header." );
					return;
				}

				uint atomSize = ReadUInt32BigEndian( header, 0 );
				string atomType = new string( new[] { ( char )header[ 4 ], ( char )header[ 5 ], ( char )header[ 6 ], ( char )header[ 7 ] } );
				Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] M4A: first atom='{atomType}', size={atomSize}" );

				if( atomType != "ftyp" )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[PlayFileCore] M4A warning: MP4 container does not start with 'ftyp'." );
				}
				else if( got >= 16 )
				{
					string majorBrand = new string( new[] { ( char )header[ 8 ], ( char )header[ 9 ], ( char )header[ 10 ], ( char )header[ 11 ] } );
					uint minorVersion = ReadUInt32BigEndian( header, 12 );
					Debug.WriteLineIf( EnableVerboseTrace, $"[PlayFileCore] M4A: majorBrand='{majorBrand}', minorVersion=0x{minorVersion:X8}" );
				}
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Scans the beginning of an MP3 stream and returns the best data start offset.
		/// </summary>

		/// <summary>
		/// Finds the size of the data chunk in a WAV file.
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		/// <returns>Size of data chunk in bytes, or 0 if not found.</returns>
		private static long FindWavDataChunkSize( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				fs.Position = 0;
				var header = new byte[ 12 ];
				if( fs.Read( header, 0, header.Length ) < 12 )
					return 0;

				long pos = 12;
				var chunkHeader = new byte[ 8 ];

				while( pos + 8 <= fs.Length )
				{
					fs.Position = pos;
					if( fs.Read( chunkHeader, 0, 8 ) < 8 )
						break;

					string chunkId = new string( new char[]
					{
									(char)chunkHeader[ 0 ],
									(char)chunkHeader[ 1 ],
									(char)chunkHeader[ 2 ],
									(char)chunkHeader[ 3 ]
					} );
					uint chunkSize = ( uint )( chunkHeader[ 4 ] | ( chunkHeader[ 5 ] << 8 ) | ( chunkHeader[ 6 ] << 16 ) | ( chunkHeader[ 7 ] << 24 ) );

					if( chunkId == "data" )
						return chunkSize;

					pos += 8 + chunkSize;
					if( chunkSize % 2 == 1 )
						pos++;
				}

				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Scans a RIFF WAV file to find the start of audio data (data chunk).
		/// </summary>
		/// <param name="fs">Open file stream positioned anywhere.</param>
		/// <returns>Byte position of first audio sample in the data chunk, or 0 if not found.</returns>
		/// <remarks>
		/// RIFF WAV format: RIFF header (12 bytes) + chunks. Each chunk has ID (4 bytes), size (4 bytes, little-endian), then data.
		/// This method skips fmt and other non-data chunks to find where the actual audio data begins.
		/// The original stream position is restored before returning.
		/// </remarks>
		private static long FindWavDataStart( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				fs.Position = 0;
				var header = new byte[ 12 ];
				if( fs.Read( header, 0, header.Length ) < 12 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[FindWavDataStart] Failed: File too small" );
					return 0;
				}

				// Check RIFF signature
				if( header[ 0 ] != 'R' || header[ 1 ] != 'I' || header[ 2 ] != 'F' || header[ 3 ] != 'F' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[FindWavDataStart] Failed: RIFF signature not found" );
					return 0;
				}

				// Check WAVE signature
				if( header[ 8 ] != 'W' || header[ 9 ] != 'A' || header[ 10 ] != 'V' || header[ 11 ] != 'E' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[FindWavDataStart] Failed: WAVE signature not found" );
					return 0;
				}

				Debug.WriteLineIf( EnableVerboseTrace, "[FindWavDataStart] RIFF/WAVE headers OK, scanning chunks..." );

				// Now scan chunks: each chunk is [4-byte ID][4-byte size LE][data][padding if size is odd]
				long pos = 12;
				var chunkHeader = new byte[ 8 ];

				while( pos + 8 <= fs.Length )
				{
					fs.Position = pos;
					if( fs.Read( chunkHeader, 0, 8 ) < 8 )
						break;

					string chunkId = new string( new char[]
					{
								(char)chunkHeader[ 0 ],
								(char)chunkHeader[ 1 ],
								(char)chunkHeader[ 2 ],
								(char)chunkHeader[ 3 ]
					} );
					uint chunkSize = ( uint )( chunkHeader[ 4 ] | ( chunkHeader[ 5 ] << 8 ) | ( chunkHeader[ 6 ] << 16 ) | ( chunkHeader[ 7 ] << 24 ) );

					Debug.WriteLineIf( EnableVerboseTrace, $"[FindWavDataStart] Found chunk '{chunkId}' at offset {pos}, size={chunkSize}" );

					if( chunkId == "fmt " )
					{
						// Parse fmt chunk to get audio format info
						if( chunkSize >= 16 )
						{
							fs.Position = pos + 8;
							var fmtData = new byte[ 16 ];
							if( fs.Read( fmtData, 0, 16 ) == 16 )
							{
								ushort audioFormat = ( ushort )( fmtData[ 0 ] | ( fmtData[ 1 ] << 8 ) );
								ushort numChannels = ( ushort )( fmtData[ 2 ] | ( fmtData[ 3 ] << 8 ) );
								uint sampleRate = ( uint )( fmtData[ 4 ] | ( fmtData[ 5 ] << 8 ) | ( fmtData[ 6 ] << 16 ) | ( fmtData[ 7 ] << 24 ) );
								ushort bitsPerSample = ( ushort )( fmtData[ 14 ] | ( fmtData[ 15 ] << 8 ) );
								Debug.WriteLineIf( EnableVerboseTrace, $"[FindWavDataStart] WAV Format: AudioFormat={audioFormat}, Channels={numChannels}, SampleRate={sampleRate}Hz, BitsPerSample={bitsPerSample}" );

								if( audioFormat != 1 )
									Debug.WriteLineIf( EnableVerboseTrace, $"[FindWavDataStart] WARNING: Audio format is {audioFormat} (not PCM=1)!" );
							}
						}
					}

					if( chunkId == "data" )
					{
						long dataStart = pos + 8;
						Debug.WriteLineIf( EnableVerboseTrace, $"[FindWavDataStart] Found data chunk! Audio starts at offset {dataStart}" );
						return dataStart;
					}

					// Skip this chunk (8-byte header + data + padding)
					pos += 8 + chunkSize;
					if( chunkSize % 2 == 1 )
						pos++; // Chunks are word-aligned
				}

				Debug.WriteLineIf( EnableVerboseTrace, "[FindWavDataStart] Warning: No data chunk found!" );
				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Waits until DREQ remains high continuously for the requested stability window.
		/// </summary>
		/// <param name="stableMs">Required consecutive milliseconds with DREQ high.</param>
		/// <param name="timeoutMs">Maximum total wait time in milliseconds.</param>
		/// <exception cref="TimeoutException">Thrown if stable high is not reached in time.</exception>
		private void WaitDreqStableHigh( int stableMs, int timeoutMs )
		{
			int waited = 0;
			int stable = 0;

			while( waited < timeoutMs )
			{
				if( this.dreqPin.Read() == GpioPinValue.High )
				{
					stable++;
					if( stable >= stableMs )
					{
						return;
					}
				}
				else
				{
					stable = 0;
				}

				Thread.Sleep( 1 );
				waited++;
			}

			throw new TimeoutException( "VS1053 DREQ not stably high." );
		}

		/// <summary>
		/// Sends dummy SPI transfers to warm up the bus after reset.
		/// </summary>
		private void PrimeSpi()
		{
			var tx = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
			var rx = new byte[ tx.Length ];

			lock( this.spiSync )
			{
				this.spiCmdDevice.TransferFullDuplex( tx, 0, tx.Length, rx, 0, rx.Length );
				this.spiCmdDevice.TransferFullDuplex( tx, 0, tx.Length, rx, 0, rx.Length );
			}
		}

		/// <summary>
		/// Selects the SCI/command channel by pulling its chip-select line low.
		/// </summary>
		private void EnableSci()
		{
			this.cmdCsPin.Write( GpioPinValue.Low );
		}

		/// <summary>
		/// Deselects the SCI/command channel by pulling its chip-select line high.
		/// </summary>
		private void DisableSci()
		{
			this.cmdCsPin.Write( GpioPinValue.High );
		}

		/// <summary>
		/// Selects the SDI/data channel by pulling its chip-select line low.
		/// </summary>
		private void EnableSdi()
		{
			this.datCsPin.Write( GpioPinValue.Low );
		}

		/// <summary>
		/// Deselects the SDI/data channel by pulling its chip-select line high.
		/// </summary>
		private void DisableSdi()
		{
			this.datCsPin.Write( GpioPinValue.High );
		}
	}
}
