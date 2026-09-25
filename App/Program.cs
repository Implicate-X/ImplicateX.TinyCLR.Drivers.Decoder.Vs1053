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

			//device.PlaySong( @"A:\short.mid" );
			device.PlaySong( @"A:\short.mp3" );
			//device.PlaySong( @"A:\sample-3s.wav" );
			//device.PlaySong( @"A:\All By Myself.flac" );
			//device.PlaySong( @"A:\usa-hymn.mid" );
			//device.PlaySong( @"A:\ogg_15s.ogg" );
			//device.PlaySong( @"A:\aac_15s.aac" );
			//device.PlaySong( @"A:\m4a_15s.m4a" );
		}
	}
}
