using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	public partial class Vs1053
	{
		/// <summary>
		/// Prepares WMA audio streams for playback by the VS1053 decoder.
		/// </summary>
		public sealed class WmaProcessor : IMediaPreprocessor
		{
			private const bool EnableVerboseTrace = false;

			/// <summary>
			/// Determines whether this processor can handle the specified file extension.
			/// </summary>
		/// <param name="extension">The file extension to check.</param>
		/// <returns><c>true</c> for <c>.wma</c>; otherwise, <c>false</c>.</returns>
		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".wma";

			/// <summary>
			/// Prepares the WMA stream from its beginning for standard-codec playback.
			/// </summary>
			/// <param name="fs">The WMA file stream to process.</param>
			/// <returns>A <see cref="MediaPayLoad"/> configured for WMA playback.</returns>
			/// <exception cref="ArgumentNullException">Thrown when <paramref name="fs"/> is <c>null</c>.</exception>
			public MediaPayLoad Process( FileStream fs )
			{
				if( fs == null )
				{
					throw new ArgumentNullException( nameof( fs ) );
				}

				fs.Position = 0;
				Debug.WriteLineIf( EnableVerboseTrace, "[WmaProcessor] WMA stream prepared from beginning." );

				return new MediaPayLoad(
					stream: fs,
					fillerBytes: 2052,
					requiresStartupMode: true,
					customClockFrequency: 0,
					startFillByte: 0x00,
					patchType: Vs1053.PatchEngine.PatchType.StandardCodec );
			}
		}
	}
}
