using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Represents a media preprocessor for AAC audio files.
	/// </summary>
	public sealed class AacProcessor : IMediaPreprocessor
	{
		private const bool EnableVerboseTrace = false;

		/// <summary>
		/// Determines whether this processor can handle the specified file extension.
		/// </summary>
		/// <param name="extension">The file extension to check.</param>
		/// <returns>True if the processor can handle the extension; otherwise, false.</returns>
		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".aac";

		/// <summary>
		/// Processes the provided file stream and returns a MediaPayLoad object.
		/// </summary>
		/// <param name="fs">The file stream to process.</param>
		/// <returns>A MediaPayLoad object representing the processed media.</returns>
		/// <exception cref="ArgumentNullException">Thrown if the file stream is null.</exception>
		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			fs.Position = 0;
			Debug.WriteLineIf( EnableVerboseTrace, "[AacProcessor] AAC stream prepared from beginning." );

			return new MediaPayLoad(
				stream: fs,
				fillerBytes: 2052,
				requiresStartupMode: true,
				customClockFrequency: 0,
				startFillByte: 0x00,
				patchType: Device.PatchEngine.PatchType.Latm );
		}
	}
}
