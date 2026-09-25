using System;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public sealed class MediaPayLoad
	(
		Stream stream,
		int fillerBytes,
		bool requiresStartupMode = true,
		int customClockFrequency = 0,
		byte startFillByte = 0x00
	)
	{
		public Stream Stream { get; } = stream;
		public int FillerBytes { get; } = fillerBytes;
		public bool RequiresStartupMode { get; } = requiresStartupMode;
		public int CustomClockFrequency { get; } = customClockFrequency;
		public byte StartFillByte { get; } = startFillByte;
	}
}
