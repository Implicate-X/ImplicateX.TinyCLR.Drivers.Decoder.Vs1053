using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Processes WAV audio files and prepares them for streaming to a VS1053 audio decoder.
	/// </summary>
	/// <remarks>
	/// This class implements the <see cref="IMediaPreprocessor"/> interface to handle WAV file processing.
	/// It parses the WAV file format (RIFF/WAVE structure), locates the audio data chunk, and returns
	/// a bounded stream that constrains reading to only the audio data portion of the file.
	/// If the WAV structure is invalid or incomplete, it falls back to using the entire file stream.
	/// 
	/// The class validates the presence of RIFF and WAVE signatures and iterates through chunk headers
	/// to find the data chunk, handling padding bytes as per the WAV format specification.
	/// </remarks>
	public sealed class WavProcessor : IMediaPreprocessor
	{
		/// <summary>
		/// Enables verbose trace output for debugging purposes. When set to <c>true</c>, detailed diagnostic information
		/// </summary>
		private const bool EnableVerboseTrace = false;

		/// <summary>
		/// Determines whether the specified file extension represents a WAV audio file.
		/// </summary>
		/// <param name="extension">The file extension to evaluate.</param>
		/// <returns><c>true</c> when <paramref name="extension" /> is <c>.wav</c>; otherwise, <c>false</c>.</returns>
		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".wav";

		/// <summary>
		/// Processes the provided WAV file stream and returns a <see cref="MediaPayLoad"/> containing the audio data.
		/// </summary>
		/// <param name="fs">The WAV file stream to process.</param>
		/// <returns>A <see cref="MediaPayLoad"/> configured for WAV playback using the provided stream.</returns>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="fs" /> is <c>null</c>.</exception>
		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			long dataStart = FindWavDataStart( fs );
			long dataChunkDataSize = FindWavDataChunkSize( fs );
			long maxBytes = dataStart + dataChunkDataSize;

			if( dataChunkDataSize <= 0 || maxBytes <= 0 || maxBytes > fs.Length )
			{
				Debug.WriteLineIf( EnableVerboseTrace, "[WavProcessor] Invalid or missing WAV data chunk. Using full stream fallback." );
				fs.Position = 0;
				return new MediaPayLoad( fs, 2052, true, 0, 0x00, Device.PatchEngine.PatchType.StandardCodec );
			}

			Debug.WriteLineIf( EnableVerboseTrace, $"[WavProcessor] WAV bounded stream prepared: 0..{maxBytes}" );
			Stream bounded = new BoundedReadStream( fs, maxBytes );

			return new MediaPayLoad( bounded, 2052, true, 0, 0x00, Device.PatchEngine.PatchType.StandardCodec );
		}

		/// <summary>
		/// Finds the size of the "data" chunk in a WAV file stream by parsing the RIFF/WAVE structure.
		/// </summary>
		/// <param name="fs">The WAV file stream to evaluate.</param>
		/// <returns>The size of the "data" chunk in bytes, or 0 if not found.</returns>
		private static long FindWavDataChunkSize( FileStream fs )
		{
			long original = fs.Position;

			try
			{
				fs.Position = 0;
				var header = new byte[ 12 ];

				if( fs.Read( header, 0, header.Length ) < 12 )
					return 0;

				long pos = 12;
				var chunkHeader = new byte[ 8 ];

				while( pos + 8 <= fs.Length )
				{
					fs.Position = pos;

					if( fs.Read( chunkHeader, 0, 8 ) < 8 )
						break;

					string chunkId = new string( new char[]
					{
						(char)chunkHeader[ 0 ],
						(char)chunkHeader[ 1 ],
						(char)chunkHeader[ 2 ],
						(char)chunkHeader[ 3 ]
					} );

					uint chunkSize = ( uint )( chunkHeader[ 4 ]
						| ( chunkHeader[ 5 ] << 8 )
						| ( chunkHeader[ 6 ] << 16 )
						| ( chunkHeader[ 7 ] << 24 ) );

					if( chunkId == "data" )
						return chunkSize;

					pos += 8 + chunkSize;
					if( chunkSize % 2 == 1 )
						pos++;
				}

				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// Finds the starting position of the "data" chunk in a WAV file stream by parsing the RIFF/WAVE structure.
		/// </summary>
		/// <param name="fs">The WAV file stream to evaluate.</param>
		/// <returns>The starting position of the "data" chunk in bytes, or 0 if not found.</returns>
		private static long FindWavDataStart( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				fs.Position = 0;
				var header = new byte[ 12 ];
				if( fs.Read( header, 0, header.Length ) < 12 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[WavProcessor] Failed: file too small." );
					return 0;
				}

				if( header[ 0 ] != 'R' || header[ 1 ] != 'I' || header[ 2 ] != 'F' || header[ 3 ] != 'F' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[WavProcessor] Failed: RIFF signature not found." );
					return 0;
				}

				if( header[ 8 ] != 'W' || header[ 9 ] != 'A' || header[ 10 ] != 'V' || header[ 11 ] != 'E' )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[WavProcessor] Failed: WAVE signature not found." );
					return 0;
				}

				long pos = 12;
				var chunkHeader = new byte[ 8 ];
				while( pos + 8 <= fs.Length )
				{
					fs.Position = pos;
					if( fs.Read( chunkHeader, 0, 8 ) < 8 )
						break;

					string chunkId = new string( new char[]
					{
						(char)chunkHeader[ 0 ],
						(char)chunkHeader[ 1 ],
						(char)chunkHeader[ 2 ],
						(char)chunkHeader[ 3 ]
					} );

					uint chunkSize = ( uint )( chunkHeader[ 4 ]
						| ( chunkHeader[ 5 ] << 8 )
						| ( chunkHeader[ 6 ] << 16 )
						| ( chunkHeader[ 7 ] << 24 ) );

					if( chunkId == "data" )
					{
						return pos + 8;
					}

					pos += 8 + chunkSize;
					if( chunkSize % 2 == 1 )
						pos++;
				}

				return 0;
			}
			finally
			{
				fs.Position = original;
			}
		}

		/// <summary>
		/// A stream wrapper that limits reading to a specified number of bytes from the underlying source stream.
		/// </summary>
		private sealed class BoundedReadStream : Stream
		{
			private readonly FileStream source;
			private readonly long limit;
			private long emitted;

			/// <summary>
			/// Initializes a new instance of the <see cref="BoundedReadStream"/> class<br/>
			/// that wraps the specified source stream and limits reading to the specified number of bytes.
			/// </summary>
			/// <param name="source">The source <see cref="FileStream"/> to read from.</param>
			/// <param name="limit">The maximum number of bytes to read from the source stream.</param>
			public BoundedReadStream( FileStream source, long limit )
			{
				this.source = source;
				this.limit = limit;
				this.source.Position = 0;
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => this.limit;

			/// <summary>
			/// Gets the current position within the bounded stream, which represents the number of bytes emitted so far.
			/// </summary>
			public override long Position
			{
				get => this.emitted;
				set => throw new NotSupportedException();
			}

			/// <summary>
			/// Reads a sequence of bytes from the bounded stream and advances the position within the stream by the number of bytes read.
			/// </summary>
			/// <param name="buffer">The buffer to read the bytes into.</param>
			/// <param name="offset">The zero-based byte offset in the buffer at which to begin storing the data read from the stream.</param>
			/// <param name="count">The maximum number of bytes to read from the stream.</param>
			/// <returns>The total number of bytes read into the buffer.</returns>
			public override int Read( byte[] buffer, int offset, int count )
			{
				if( this.emitted >= this.limit )
				{
					return 0;
				}

				long remaining = this.limit - this.emitted;
				int toRead = ( int )Math.Min( count, remaining );
				int read = this.source.Read( buffer, offset, toRead );
				if( read > 0 )
				{
					this.emitted += read;
				}

				return read;
			}

			public override void Flush() { }
			public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();
			public override void SetLength( long value ) => throw new NotSupportedException();
			public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		}
	}
}
