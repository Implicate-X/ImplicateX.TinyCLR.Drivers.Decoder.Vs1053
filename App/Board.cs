#define FEATHER
#define FEATHER_SPI6
//#define DUINO
//#define DUINO_SPI6
#define TEST_FLAC
//#define HEALTH_CHECK

using System.Diagnostics;
using GHIElectronics.TinyCLR.Pins;

namespace ImplicateX.TinyCLR.Drivers.Decoder;

public class Board
{
#if FEATHER_SPI6
	public static readonly string SpiControllerName = FEZFeather.SpiBus.Spi6;
#elif FEATHER_SPI4
    public static readonly string SpiControllerName = FEZFeather.SpiBus.Spi4;
#elif DUINO_SPI6
	public static readonly string SpiControllerName = FEZDuino.SpiBus.Spi6;
	#endif

#if FEATHER
	public static class ControllerPin
	{
		public const int CodecCS = FEZFeather.GpioPin.PE0;
		public const int CodecDCS = FEZFeather.GpioPin.PE1;
		public const int CodecDREQ = FEZFeather.GpioPin.PE7;
		public const int CodecReset = FEZFeather.GpioPin.PE8;
	}
#elif DUINO
	public static class ControllerPin
	{
		public const int CodecCS = FEZDuino.GpioPin.PC5;
		public const int CodecDCS = FEZDuino.GpioPin.PC4;
		public const int CodecDREQ = FEZDuino.GpioPin.PC6;
		public const int CodecReset = FEZDuino.GpioPin.PC7;
	}
#endif

	private readonly Vs1053 audioCodec = null!;

	public Board()
	{
		_ = new Storage();

		audioCodec = new Vs1053(
			spiControllerName: SpiControllerName,
			cmdCsPinID: ControllerPin.CodecCS,
			datCsPinID: ControllerPin.CodecDCS,
			dreqPinID: ControllerPin.CodecDREQ,
			resetPinID: ControllerPin.CodecReset );

		audioCodec.Initialize();

#if HEALTH_CHECK
		Vs1053.HealthCheckReport healthReport = 
			audioCodec.CheckHealth( includeSineTest: true );

		Debug.WriteLine
		(
			$"[VS1053 Health] Overall: {( healthReport.IsHealthy ? "HEALTHY" : "UNHEALTHY" )}," +
			$"Code={( int )healthReport.OverallCode} ({healthReport.OverallCode})"
		);

		for( int i = 0; i < healthReport.Results.Length; i++ )
		{
			Vs1053.HealthTestResult result = healthReport.Results[ i ];

			Debug.WriteLine
			(
				$"[VS1053 Health] {result.Test}: Code={( int )result.Code} ({result.Code}), " +
				$"Passed={result.Passed}, Message={result.Message}"
			);
		}
#else
		audioCodec.SetVolume( 10, 10 );

		#if TEST_MIDI
		audioCodec.PlaySong( @"A:\Usa-Hymn.mid" );
		#endif
		#if TEST_MP3
		audioCodec.PlaySong( @"A:\Abba- Thank You for the Music.mp3" );
		#endif
		#if TEST_FLAC
		audioCodec.PlaySong( @"A:\All by Myself.flac" );
#endif
#if TEST_WAV
		audioCodec.PlaySong( @"A:\sample-12s.wav" );
#endif
#if TEST_OGG
		audioCodec.PlaySong( @"A:\ogg_15s.ogg" );
#endif
#if TEST_AAC
		audioCodec.PlaySong( @"A:\aac_15s.aac" );
#endif
#if TEST_M4A
		audioCodec.PlaySong( @"A:\m4a_15s.m4a" );
#endif
#endif
	}
}
