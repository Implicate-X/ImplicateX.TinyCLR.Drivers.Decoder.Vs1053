using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Preprocesses DSD-based media files so they can be streamed to the VS1053 decoder
	/// using the timing and filler-byte settings required by the DSD playback path.
	/// </summary>
	public sealed class DsdProcessor : IMediaPreprocessor
	{
		private const bool EnableVerboseTrace = false;
		private const int DsdDataSPIFrequency = 12_000_000;
		private const byte DsdEndFillByte = 0x55;

		/// <summary>
		/// Determines whether the specified file extension represents a supported DSD container.
		/// </summary>
		/// <param name="extension">The file extension to evaluate, including the leading period.</param>
		/// <returns>
		/// <see langword="true"/> when the extension is <c>.dsd</c>, <c>.dsf</c>, or <c>.dff>;
		/// otherwise, <see langword="false"/>.
		/// </returns>
		public bool CanProcess( string extension )
			=> extension != null && ( extension.ToLower() == ".dsd" || extension.ToLower() == ".dsf" || extension.ToLower() == ".dff" );

		/// <summary>
		/// Creates a media payload for a DSD stream using VS1053-specific playback settings.
		/// </summary>
		/// <param name="fs">The input stream containing the DSD media data.</param>
		/// <returns>
		/// A <see cref="MediaPayLoad"/> configured with the stream, filler bytes, startup mode,
		/// SPI clock frequency, and fill byte required for DSD playback.
		/// </returns>
		/// <exception cref="ArgumentNullException"><paramref name="fs"/> is <see langword="null"/>.</exception>
		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			LogDsdContainerInfo( fs );
			fs.Position = 0;

			return new MediaPayLoad(
				stream: fs,
				fillerBytes: 12288,
				requiresStartupMode: false,
				customClockFrequency: DsdDataSPIFrequency,
				startFillByte: DsdEndFillByte,
				patchType: Device.PatchEngine.PatchType.Dsd );
		}

		/// <summary>
		/// Logs diagnostic information about the DSD container format to the debug output, if verbose tracing is enabled.
		/// </summary>
		/// <param name="fs">The input stream containing the DSD media data.</param>
		private static void LogDsdContainerInfo( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				if( fs.Length < 64 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] File too small for container diagnostics." );
					return;
				}

				var header = new byte[ 96 ];
				fs.Position = 0;
				int got = fs.Read( header, 0, header.Length );
				if( got < 64 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] Unable to read container header." );
					return;
				}

				if( header[ 0 ] == ( byte )'D' && header[ 1 ] == ( byte )'S' && header[ 2 ] == ( byte )'D' && header[ 3 ] == ( byte )' ' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] DSD container detected: DSF" );

					if( header[ 28 ] == ( byte )'f' && header[ 29 ] == ( byte )'m' && header[ 30 ] == ( byte )'t' && header[ 31 ] == ( byte )' ' )
					{
						uint channels = ReadUInt32LittleEndian( header, 52 );
						uint sampleRate = ReadUInt32LittleEndian( header, 56 );
						uint bitsPerSample = ReadUInt32LittleEndian( header, 60 );
						Debug.WriteLineIf( EnableVerboseTrace, $"[DsdProcessor] DSF fmt: Channels={channels}, SampleRate={sampleRate}, BitsPerSample={bitsPerSample}" );

						if( channels != 2 || sampleRate != 2822400 )
						{
							Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] Warning: VS1053 DSD patch expects stereo DSD64 (2 channels, 2822400 Hz)." );
						}
					}
				}
				else if( header[ 0 ] == ( byte )'F' && header[ 1 ] == ( byte )'R' && header[ 2 ] == ( byte )'M' && header[ 3 ] == ( byte )'8' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] DSD container detected: DFF" );
				}
				else
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[DsdProcessor] Warning: container signature is neither DSF nor DFF." );
				}
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Reads a 32-bit unsigned integer from a byte array in little-endian order, starting at the specified offset.
		/// </summary>
		/// <param name="buffer">The byte array containing the data.</param>
		/// <param name="offset">The zero-based byte offset in the array at which to begin reading.</param>
		/// <returns>The 32-bit unsigned integer read from the array.</returns>
		private static uint ReadUInt32LittleEndian( byte[] buffer, int offset )
		{
			return ( uint )( buffer[ offset ]
				| ( buffer[ offset + 1 ] << 8 )
				| ( buffer[ offset + 2 ] << 16 )
				| ( buffer[ offset + 3 ] << 24 ) );
		}
	}
}
