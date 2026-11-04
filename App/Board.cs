using System.Diagnostics;
using GHIElectronics.TinyCLR.Pins;
using GHIElectronics.TinyCLR.Devices.Gpio;

namespace ImplicateX.TinyCLR.Drivers.Decoder;

public class Board
{
	private Vs1053 vs1053 = null!;

	public Board()
	{
		_ = new Storage();

		vs1053 = new Vs1053(
			spiControllerName: FEZDuino.SpiBus.Spi6,
			cmdCsPinID: FEZDuino.GpioPin.PC5,
			datCsPinID: FEZDuino.GpioPin.PC4,
			dreqPinID: FEZDuino.GpioPin.PC6,
			resetPinID: FEZDuino.GpioPin.PC7 );
			//gpio0PinID: FEZDuino.GpioPin.PA1,
			//gpio1PinID: FEZDuino.GpioPin.PA2,
			// uartControllerName: FEZDuino.UartPort.Uart1

		vs1053.Initialize();

		Vs1053.HealthCheckReport healthReport = vs1053.CheckHealth();
		Debug.WriteLine( $"[VS1053 Health] Overall: {( healthReport.IsHealthy ? "HEALTHY" : "UNHEALTHY" )}, Code={( int )healthReport.OverallCode} ({healthReport.OverallCode})" );

		for( int i = 0; i < healthReport.Results.Length; i++ )
		{
			Vs1053.HealthTestResult result = healthReport.Results[ i ];
			Debug.WriteLine( $"[VS1053 Health] {result.Test}: Code={( int )result.Code} ({result.Code}), Passed={result.Passed}, Message={result.Message}" );
		}

		vs1053.SetVolume( 10, 10 );
		//vs1053.DumpSciRegisters();

		//vs1053.PlaySong( @"A:\Usa-Hymn.mid" );
		//vs1053.PlaySong( @"A:\Abba- Thank You for the Music.mp3" );
		vs1053.PlaySong( @"A:\All by Myself.flac" );
		//vs1053.PlaySong( @"A:\sample-12s.wav" );
		//vs1053.PlaySong( @"A:\ogg_15s.ogg" );
		//vs1053.PlaySong( @"A:\aac_15s.aac" );
		//vs1053.PlaySong( @"A:\m4a_15s.m4a" );
	}
}
