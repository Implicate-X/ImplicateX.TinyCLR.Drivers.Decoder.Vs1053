using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public sealed class FlacProcessor : IMediaPreprocessor
	{
		private const bool EnableVerboseTrace = false;
		private const int FlacDataSPIFrequency = 12_000_000;

		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".flac";

		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			fs.Position = 0;
			Debug.WriteLineIf( EnableVerboseTrace, "[FlacProcessor] FLAC stream prepared from beginning (fLaC header)." );

			return new MediaPayLoad(
				stream: fs,
				fillerBytes: 12288,
				requiresStartupMode: false,
				customClockFrequency: FlacDataSPIFrequency,
				startFillByte: 0x00,
				patchType: Device.PatchEngine.PatchType.Flac );
		}
	}
}
