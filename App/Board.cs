using GHIElectronics.TinyCLR.Pins;

namespace ImplicateX.TinyCLR.Drivers.Decoder;

public class Board
{
	private Vs1053 vs1053 = null!;

	public Board()
	{
		_ = new Storage();

		vs1053 = new Vs1053(
			uartControllerName: FEZDuino.UartPort.Uart1,
			spiControllerName: FEZDuino.SpiBus.Spi6,
			cmdCsPinID: FEZDuino.GpioPin.PC5,
			datCsPinID: FEZDuino.GpioPin.PC6,
			dreqPinID: FEZDuino.GpioPin.PC4,
			resetPinID: FEZDuino.GpioPin.PC7,
			gpio0PinID: FEZDuino.GpioPin.PA1,
			gpio1PinID: FEZDuino.GpioPin.PA2 );
		vs1053.Initialize();
		vs1053.DumpSciRegisters();
		vs1053.RunStartupSineTest();



		//vs1053.PlaySong( @"A:\Usa-Hymn.mid" );
		vs1053.PlaySong( @"A:\Abba- Thank You for the Music.mp3" );
		vs1053.PlaySong( @"A:\All by Myself.flac" );
		vs1053.PlaySong( @"A:\sample-12s.wav" );
		vs1053.PlaySong( @"A:\ogg_15s.ogg" );
		vs1053.PlaySong( @"A:\aac_15s.aac" );
		vs1053.PlaySong( @"A:\m4a_15s.m4a" );
	}
}
