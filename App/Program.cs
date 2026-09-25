using GHIElectronics.TinyCLR.Pins;
using ImplicateX.TinyCLR.Drivers.Decoder.Vs1053;

namespace Vs1053App
{
	internal class Program
	{
		private static Device device = null!;

		static void Main()
		{
			_ = new Storage();

			device = new Device(
				spiControllerName: FEZDuino.SpiBus.Spi6,
				cmdCsPinID: FEZDuino.GpioPin.PC4,
				datCsPinID: FEZDuino.GpioPin.PC5,
				dreqPinID: FEZDuino.GpioPin.PC6,
				resetPinID: FEZDuino.GpioPin.PC7,
				gpio0PinID: FEZDuino.GpioPin.PA1,
				gpio1PinID: FEZDuino.GpioPin.PA2 );

			device.Initialize();

			device.PlaySong( @"A:\sample-100kb.mp3" );
			device.PlaySong( @"A:\sample-3s.wav" );
			device.PlaySong( @"A:\sample-1mb.flac" );
			device.PlaySong( @"A:\ogg_15s.ogg" );
			device.PlaySong( @"A:\aac_15s.aac" );
			device.PlaySong( @"A:\m4a_15s.m4a" );
		}
	}
}
