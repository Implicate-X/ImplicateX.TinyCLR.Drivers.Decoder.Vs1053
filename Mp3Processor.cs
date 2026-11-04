using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	public partial class Vs1053
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
					startFillByte: 0x00,
					patchType: Vs1053.PatchEngine.PatchType.StandardCodec );
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

					// Fast path: handle ID3v2 tag at file start without allocating a large probe buffer.
					if( scanLen >= 10 )
					{
						var id3Header = new byte[ 10 ];
						int id3Read = fs.Read( id3Header, 0, id3Header.Length );

						if( id3Read == 10
							&& id3Header[ 0 ] == ( byte )'I'
							&& id3Header[ 1 ] == ( byte )'D'
							&& id3Header[ 2 ] == ( byte )'3' )
						{
							int size = ( id3Header[ 6 ] << 21 ) | ( id3Header[ 7 ] << 14 ) | ( id3Header[ 8 ] << 7 ) | id3Header[ 9 ];
							long pos = 10 + size;
							if( pos < fs.Length )
							{
								return pos;
							}
						}
					}

					fs.Position = 0;
					const int ProbeChunkSize = 512;
					var probe = new byte[ ProbeChunkSize + 1 ];
					int carry = 0;
					long scanned = 0;

					while( scanned < scanLen )
					{
						int toRead = ( int )Math.Min( ProbeChunkSize, scanLen - scanned );
						int got = fs.Read( probe, carry, toRead );
						if( got <= 0 )
						{
							break;
						}

						int total = carry + got;
						for( int i = 0; i <= total - 2; i++ )
						{
							if( probe[ i ] == 0xFF && ( probe[ i + 1 ] & 0xE0 ) == 0xE0 )
							{
								return ( scanned - carry ) + i;
							}
						}

						if( total > 0 )
						{
							probe[ 0 ] = probe[ total - 1 ];
							carry = 1;
						}
						else
						{
							carry = 0;
						}

						scanned += got;
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
}
