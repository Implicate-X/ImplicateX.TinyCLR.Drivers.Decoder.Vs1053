using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Preprocesses MP3 files before they are passed to the decoder.
	/// </summary>
	/// <remarks>
	/// The processor attempts to skip leading metadata by locating either the first MP3 frame sync
	/// or an <c>ID3</c> tag header and positioning the stream at the detected audio data start.
	/// </remarks>
	public sealed class Mp3Processor : IMediaPreprocessor
	{
		/// <summary>
		/// Enables verbose trace output for debugging purposes.
		/// </summary>
		private const bool EnableVerboseTrace = false;

		/// <summary>
		/// Determines whether the specified file extension represents an MP3 file.
		/// </summary>
		/// <param name="extension">The file extension to evaluate.</param>
		/// <returns><c>true</c> when <paramref name="extension" /> is <c>.mp3</c>; otherwise, <c>false</c>.</returns>
		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".mp3";

		/// <summary>
		/// Prepares an MP3 stream for playback by positioning it at the beginning of audio data.
		/// </summary>
		/// <param name="fs">The MP3 file stream to process.</param>
		/// <returns>
		/// A <see cref="MediaPayLoad" /> configured for MP3 playback using the provided stream.
		/// </returns>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="fs" /> is <c>null</c>.</exception>
		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			long dataStart = FindMp3DataStart( fs );
			Debug.WriteLineIf( EnableVerboseTrace, $"[Mp3Processor] MP3 data start: {dataStart}" );

			if( dataStart < 0 || dataStart >= fs.Length )
			{
				Debug.WriteLineIf( EnableVerboseTrace, "[Mp3Processor] Invalid start offset, fallback to 0." );
				dataStart = 0;
			}

			fs.Position = dataStart;

			return new MediaPayLoad(
				stream: fs,
				fillerBytes: 2052,
				requiresStartupMode: true,
				customClockFrequency: 0,
				startFillByte: 0x00 );
		}

		/// <summary>
		/// Searches the beginning of the stream for the first MP3 frame sync or ID3 tag and returns
		/// the detected audio start position.
		/// </summary>
		/// <param name="fs">The stream to scan.</param>
		/// <returns>
		/// The byte offset of the first detected MP3 data, or <c>0</c> if no usable marker is found.
		/// </returns>
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

				for( int i = 0; i <= got - 2; i++ )
				{
					if( probe[ i ] == 0xFF && ( probe[ i + 1 ] & 0xE0 ) == 0xE0 )
					{
						return i;
					}
				}

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
	}
}
