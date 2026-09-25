using System;
using System.Diagnostics;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public sealed class M4AProcessor : IMediaPreprocessor
	{
		private const bool EnableVerboseTrace = false;

		private struct StscEntry
		{
			public uint FirstChunk;
			public uint SamplesPerChunk;
			public uint SampleDescriptionIndex;
		}

		private sealed class Mp4AacTrackInfo
		{
			public int SampleRate;
			public int ChannelCount;
			public uint[] SampleSizes = new uint[ 0 ];
			public long[] ChunkOffsets = new long[ 0 ];
			public StscEntry[] SampleToChunk = new StscEntry[ 0 ];
		}

		public bool CanProcess( string extension )
			=> extension != null && extension.ToLower() == ".m4a";

		public MediaPayLoad Process( FileStream fs )
		{
			if( fs == null )
			{
				throw new ArgumentNullException( nameof( fs ) );
			}

			LogMp4ContainerInfo( fs );

			if( TryBuildMp4AacTrackInfo( fs, out Mp4AacTrackInfo trackInfo ) )
			{
				Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] AAC track detected (samples={trackInfo.SampleSizes.Length}, sampleRate={trackInfo.SampleRate}, channels={trackInfo.ChannelCount})." );

				Stream adtsStream = BuildAdtsStream( fs, trackInfo );
				if( adtsStream != null )
				{
					return new MediaPayLoad(
						stream: adtsStream,
						fillerBytes: 2052,
						requiresStartupMode: true,
						customClockFrequency: 0,
						startFillByte: 0x00
					);
				}

				Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] ADTS conversion failed; using original container stream." );
			}
			else
			{
				Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] AAC track parsing failed; using original container stream." );
			}

			fs.Position = 0;
			return new MediaPayLoad(
				stream: fs,
				fillerBytes: 2052,
				requiresStartupMode: true,
				customClockFrequency: 0,
				startFillByte: 0x00
			);
		}

		private static Stream BuildAdtsStream( FileStream fs, Mp4AacTrackInfo trackInfo )
		{
			if( trackInfo == null || trackInfo.SampleSizes == null || trackInfo.SampleSizes.Length == 0 || trackInfo.ChunkOffsets == null || trackInfo.ChunkOffsets.Length == 0 )
			{
				return null;
			}

			if( trackInfo.SampleRate <= 0 || trackInfo.ChannelCount < 1 || trackInfo.ChannelCount > 7 )
			{
				return null;
			}

			if( trackInfo.SampleToChunk == null || trackInfo.SampleToChunk.Length == 0 )
			{
				return null;
			}

			return new AdtsTranscodingStream( fs, trackInfo );
		}

		private sealed class AdtsTranscodingStream : Stream
		{
			private readonly FileStream source;
			private readonly Mp4AacTrackInfo trackInfo;
			private readonly byte[] headerBuffer = new byte[ 7 ];
			private readonly long streamLength;

			private int chunkIndex;
			private uint samplesPerChunk;
			private uint sampleInChunk;
			private int sampleIndex;
			private long samplePosition;
			private int sampleBytesRemaining;
			private int headerOffset;
			private int headerBytesRemaining;
			private bool eos;
			private long emittedBytes;

			public AdtsTranscodingStream( FileStream source, Mp4AacTrackInfo trackInfo )
			{
				this.source = source ?? throw new ArgumentNullException( nameof( source ) );
				this.trackInfo = trackInfo ?? throw new ArgumentNullException( nameof( trackInfo ) );

				long payloadBytes = 0;
				for( int i = 0; i < trackInfo.SampleSizes.Length; i++ )
				{
					payloadBytes += trackInfo.SampleSizes[ i ];
				}

				this.streamLength = payloadBytes + ( long )trackInfo.SampleSizes.Length * 7L;
				Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] ADTS transcoding stream ready (length={this.streamLength} bytes)." );
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => this.streamLength;

			public override long Position
			{
				get => this.emittedBytes;
				set => throw new NotSupportedException();
			}

			public override int Read( byte[] buffer, int offset, int count )
			{
				if( buffer == null )
				{
					throw new ArgumentNullException( nameof( buffer ) );
				}

				if( offset < 0 || count < 0 || offset + count > buffer.Length )
				{
					throw new ArgumentOutOfRangeException();
				}

				if( count == 0 || this.eos )
				{
					return 0;
				}

				int written = 0;
				while( count > 0 )
				{
					if( !EnsureCurrentSamplePrepared() )
					{
						break;
					}

					if( this.headerBytesRemaining > 0 )
					{
						int toCopy = Math.Min( count, this.headerBytesRemaining );
						int headerStart = this.headerOffset;
						Array.Copy( this.headerBuffer, headerStart, buffer, offset, toCopy );
						this.headerOffset += toCopy;
						this.headerBytesRemaining -= toCopy;
						offset += toCopy;
						count -= toCopy;
						written += toCopy;
						this.emittedBytes += toCopy;
						continue;
					}

					if( this.sampleBytesRemaining > 0 )
					{
						if( this.samplePosition < 0 || this.samplePosition >= this.source.Length )
						{
							Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] Invalid sample read position {this.samplePosition}." );
							this.eos = true;
							break;
						}

						this.source.Position = this.samplePosition;
						int toRead = Math.Min( count, this.sampleBytesRemaining );
						int read = this.source.Read( buffer, offset, toRead );
						if( read <= 0 )
						{
							Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] Unexpected end of source while streaming AAC sample." );
							this.eos = true;
							break;
						}

						this.samplePosition += read;
						this.sampleBytesRemaining -= read;
						offset += read;
						count -= read;
						written += read;
						this.emittedBytes += read;
						continue;
					}
				}

				return written;
			}

			private bool EnsureCurrentSamplePrepared()
			{
				if( this.eos )
				{
					return false;
				}

				if( this.headerBytesRemaining > 0 || this.sampleBytesRemaining > 0 )
				{
					return true;
				}

				if( this.sampleIndex >= this.trackInfo.SampleSizes.Length )
				{
					this.eos = true;
					return false;
				}

				if( this.samplesPerChunk == 0 || this.sampleInChunk >= this.samplesPerChunk )
				{
					if( this.chunkIndex >= this.trackInfo.ChunkOffsets.Length )
					{
						this.eos = true;
						return false;
					}

					this.samplesPerChunk = GetSamplesPerChunk( this.trackInfo.SampleToChunk, ( uint )( this.chunkIndex + 1 ) );
					if( this.samplesPerChunk == 0 )
					{
						this.eos = true;
						return false;
					}

					this.sampleInChunk = 0;
					this.samplePosition = this.trackInfo.ChunkOffsets[ this.chunkIndex ];
					if( this.samplePosition < 0 || this.samplePosition >= this.source.Length )
					{
						Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] Chunk offset out of bounds: {this.samplePosition}." );
						this.eos = true;
						return false;
					}
					this.chunkIndex++;
				}

				int sampleSize = ( int )this.trackInfo.SampleSizes[ this.sampleIndex++ ];
				if( sampleSize <= 0 )
				{
					this.eos = true;
					return false;
				}

				if( !TryBuildAdtsHeader( this.headerBuffer, this.trackInfo.SampleRate, this.trackInfo.ChannelCount, sampleSize ) )
				{
					this.eos = true;
					return false;
				}

				if( this.samplePosition + sampleSize > this.source.Length )
				{
					Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] Sample range out of bounds: pos={this.samplePosition}, size={sampleSize}, sourceLen={this.source.Length}." );
					this.eos = true;
					return false;
				}

				this.headerOffset = 0;
				this.headerBytesRemaining = this.headerBuffer.Length;
				this.sampleBytesRemaining = sampleSize;
				this.sampleInChunk++;
				return true;
			}

			public override void Flush()
			{
			}

			public override long Seek( long offset, SeekOrigin origin )
			{
				throw new NotSupportedException();
			}

			public override void SetLength( long value )
			{
				throw new NotSupportedException();
			}

			public override void Write( byte[] buffer, int offset, int count )
			{
				throw new NotSupportedException();
			}
		}

		private static uint GetSamplesPerChunk( StscEntry[] entries, uint chunkNumber )
		{
			if( entries == null || entries.Length == 0 )
			{
				return 0;
			}

			StscEntry current = entries[ 0 ];
			for( int i = 1; i < entries.Length; i++ )
			{
				if( chunkNumber < entries[ i ].FirstChunk )
				{
					break;
				}

				current = entries[ i ];
			}

			return current.SamplesPerChunk;
		}

		private static bool TryBuildAdtsHeader( byte[] header, int sampleRate, int channelCount, int sampleSize )
		{
			if( header == null || header.Length < 7 )
			{
				return false;
			}

			if( sampleSize < 0 || channelCount < 1 || channelCount > 7 )
			{
				return false;
			}

			int sampleRateIndex = GetAacSampleRateIndex( sampleRate );
			if( sampleRateIndex < 0 )
			{
				return false;
			}

			int profile = 1;
			int frameLength = sampleSize + 7;
			if( frameLength > 0x1FFF )
			{
				return false;
			}

			header[ 0 ] = 0xFF;
			header[ 1 ] = 0xF1;
			header[ 2 ] = ( byte )( ( profile << 6 ) | ( sampleRateIndex << 2 ) | ( ( channelCount >> 2 ) & 0x01 ) );
			header[ 3 ] = ( byte )( ( ( channelCount & 0x03 ) << 6 ) | ( ( frameLength >> 11 ) & 0x03 ) );
			header[ 4 ] = ( byte )( ( frameLength >> 3 ) & 0xFF );
			header[ 5 ] = ( byte )( ( ( frameLength & 0x07 ) << 5 ) | 0x1F );
			header[ 6 ] = 0xFC;
			return true;
		}

		private static int GetAacSampleRateIndex( int sampleRate )
		{
			return sampleRate switch
			{
				96000 => 0,
				88200 => 1,
				64000 => 2,
				48000 => 3,
				44100 => 4,
				32000 => 5,
				24000 => 6,
				22050 => 7,
				16000 => 8,
				12000 => 9,
				11025 => 10,
				8000 => 11,
				7350 => 12,
				_ => -1,
			};
		}

		private static bool TryBuildMp4AacTrackInfo( FileStream fs, out Mp4AacTrackInfo trackInfo )
		{
			trackInfo = null;

			long original = fs.Position;
			try
			{
				if( !FindAtomInRange( fs, 0, fs.Length, "moov", out _, out long moovPayloadStart, out long moovAtomEnd ) )
				{
					return false;
				}

				long searchPos = moovPayloadStart;
				while( FindAtomInRange( fs, searchPos, moovAtomEnd, "trak", out _, out long trakPayloadStart, out long trakAtomEnd ) )
				{
					if( TryParseMp4AacTrackInfo( fs, trakPayloadStart, trakAtomEnd, out trackInfo ) )
					{
						return true;
					}

					searchPos = trakAtomEnd;
				}

				return false;
			}
			finally
			{
				fs.Position = original;
			}
		}

		private static bool TryParseMp4AacTrackInfo( FileStream fs, long trakPayloadStart, long trakAtomEnd, out Mp4AacTrackInfo trackInfo )
		{
			trackInfo = null;

			if( !FindAtomInRange( fs, trakPayloadStart, trakAtomEnd, "mdia", out _, out long mdiaPayloadStart, out long mdiaAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, mdiaPayloadStart, mdiaAtomEnd, "hdlr", out _, out long hdlrPayloadStart, out long hdlrAtomEnd ) )
			{
				return false;
			}

			fs.Position = hdlrPayloadStart + 8;
			var handler = new byte[ 4 ];
			if( fs.Read( handler, 0, handler.Length ) < handler.Length )
			{
				return false;
			}

			if( handler[ 0 ] != ( byte )'s' || handler[ 1 ] != ( byte )'o' || handler[ 2 ] != ( byte )'u' || handler[ 3 ] != ( byte )'n' )
			{
				return false;
			}

			if( !FindAtomInRange( fs, mdiaPayloadStart, mdiaAtomEnd, "minf", out _, out long minfPayloadStart, out long minfAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, minfPayloadStart, minfAtomEnd, "stbl", out _, out long stblPayloadStart, out long stblAtomEnd ) )
			{
				return false;
			}

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsd", out _, out long stsdPayloadStart, out _ ) )
			{
				return false;
			}

			fs.Position = stsdPayloadStart;
			var stsdHeader = new byte[ 48 ];
			if( fs.Read( stsdHeader, 0, stsdHeader.Length ) < 44 )
			{
				return false;
			}

			uint entryCount = ReadUInt32BigEndian( stsdHeader, 4 );
			if( entryCount < 1 )
			{
				return false;
			}

			string sampleEntryType = new string( new[] { ( char )stsdHeader[ 12 ], ( char )stsdHeader[ 13 ], ( char )stsdHeader[ 14 ], ( char )stsdHeader[ 15 ] } );
			if( sampleEntryType != "mp4a" )
			{
				return false;
			}

			var track = new Mp4AacTrackInfo
			{
				ChannelCount = ReadUInt16BigEndian( stsdHeader, 32 ),
				SampleRate = ( int )( ReadUInt32BigEndian( stsdHeader, 40 ) >> 16 )
			};

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsz", out _, out long stszPayloadStart, out _ ) )
			{
				return false;
			}

			fs.Position = stszPayloadStart;
			var stszHeader = new byte[ 12 ];
			if( fs.Read( stszHeader, 0, stszHeader.Length ) < stszHeader.Length )
			{
				return false;
			}

			uint constantSampleSize = ReadUInt32BigEndian( stszHeader, 4 );
			uint sampleCount = ReadUInt32BigEndian( stszHeader, 8 );
			if( sampleCount == 0 )
			{
				return false;
			}

			track.SampleSizes = new uint[ sampleCount ];
			if( constantSampleSize != 0 )
			{
				for( int i = 0; i < track.SampleSizes.Length; i++ )
				{
					track.SampleSizes[ i ] = constantSampleSize;
				}
			}
			else
			{
				var sizeBuffer = new byte[ 4 ];
				for( int i = 0; i < track.SampleSizes.Length; i++ )
				{
					if( fs.Read( sizeBuffer, 0, sizeBuffer.Length ) < sizeBuffer.Length )
					{
						return false;
					}

					track.SampleSizes[ i ] = ReadUInt32BigEndian( sizeBuffer, 0 );
				}
			}

			if( !FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stsc", out _, out long stscPayloadStart, out _ ) )
			{
				return false;
			}

			fs.Position = stscPayloadStart;
			var stscHeader = new byte[ 8 ];
			if( fs.Read( stscHeader, 0, stscHeader.Length ) < stscHeader.Length )
			{
				return false;
			}

			uint stscEntryCount = ReadUInt32BigEndian( stscHeader, 4 );
			if( stscEntryCount == 0 )
			{
				return false;
			}

			track.SampleToChunk = new StscEntry[ stscEntryCount ];
			var stscEntryBuffer = new byte[ 12 ];
			for( int i = 0; i < track.SampleToChunk.Length; i++ )
			{
				if( fs.Read( stscEntryBuffer, 0, stscEntryBuffer.Length ) < stscEntryBuffer.Length )
				{
					return false;
				}

				track.SampleToChunk[ i ] = new StscEntry
				{
					FirstChunk = ReadUInt32BigEndian( stscEntryBuffer, 0 ),
					SamplesPerChunk = ReadUInt32BigEndian( stscEntryBuffer, 4 ),
					SampleDescriptionIndex = ReadUInt32BigEndian( stscEntryBuffer, 8 )
				};

				if( track.SampleToChunk[ i ].FirstChunk == 0 || track.SampleToChunk[ i ].SamplesPerChunk == 0 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] Invalid stsc entry encountered." );
					return false;
				}
			}

			if( FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "stco", out _, out long stcoPayloadStart, out _ ) )
			{
				fs.Position = stcoPayloadStart;
				var stcoHeader = new byte[ 8 ];
				if( fs.Read( stcoHeader, 0, stcoHeader.Length ) < stcoHeader.Length )
				{
					return false;
				}

				uint chunkCount = ReadUInt32BigEndian( stcoHeader, 4 );
				if( chunkCount == 0 )
				{
					return false;
				}

				track.ChunkOffsets = new long[ chunkCount ];
				var offsetBuffer = new byte[ 4 ];
				for( int i = 0; i < track.ChunkOffsets.Length; i++ )
				{
					if( fs.Read( offsetBuffer, 0, offsetBuffer.Length ) < offsetBuffer.Length )
					{
						return false;
					}

					track.ChunkOffsets[ i ] = ReadUInt32BigEndian( offsetBuffer, 0 );
				}
			}
			else if( FindAtomInRange( fs, stblPayloadStart, stblAtomEnd, "co64", out _, out long co64PayloadStart, out _ ) )
			{
				fs.Position = co64PayloadStart;
				var co64Header = new byte[ 8 ];
				if( fs.Read( co64Header, 0, co64Header.Length ) < co64Header.Length )
				{
					return false;
				}

				uint chunkCount = ReadUInt32BigEndian( co64Header, 4 );
				if( chunkCount == 0 )
				{
					return false;
				}

				track.ChunkOffsets = new long[ chunkCount ];
				var offsetBuffer = new byte[ 8 ];
				for( int i = 0; i < track.ChunkOffsets.Length; i++ )
				{
					if( fs.Read( offsetBuffer, 0, offsetBuffer.Length ) < offsetBuffer.Length )
					{
						return false;
					}

					track.ChunkOffsets[ i ] = ( long )ReadUInt64BigEndian( offsetBuffer, 0 );
				}
			}
			else
			{
				return false;
			}

			for( int i = 0; i < track.ChunkOffsets.Length; i++ )
			{
				if( track.ChunkOffsets[ i ] < 0 || track.ChunkOffsets[ i ] >= fs.Length )
				{
					Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] Chunk offset out of file bounds: {track.ChunkOffsets[ i ]}." );
					return false;
				}
			}

			trackInfo = track;
			return true;
		}

		private static bool FindAtomInRange( FileStream fs, long rangeStart, long rangeEnd, string targetType, out long atomStart, out long payloadStart, out long atomEnd )
		{
			atomStart = 0;
			payloadStart = 0;
			atomEnd = 0;

			long pos = rangeStart;
			while( pos + 8 <= rangeEnd )
			{
				fs.Position = pos;
				var header = new byte[ 16 ];
				int got = fs.Read( header, 0, header.Length );
				if( got < 8 )
				{
					return false;
				}

				uint size32 = ReadUInt32BigEndian( header, 0 );
				string atomType = new string( new[] { ( char )header[ 4 ], ( char )header[ 5 ], ( char )header[ 6 ], ( char )header[ 7 ] } );
				long headerSize = 8;
				long atomSize = size32;

				if( size32 == 1 )
				{
					if( got < 16 )
					{
						return false;
					}

					headerSize = 16;
					atomSize = ( long )ReadUInt64BigEndian( header, 8 );
				}
				else if( size32 == 0 )
				{
					atomSize = rangeEnd - pos;
				}

				if( atomSize < headerSize )
				{
					return false;
				}

				long currentAtomEnd = pos + atomSize;
				if( currentAtomEnd > rangeEnd )
				{
					currentAtomEnd = rangeEnd;
				}

				if( atomType == targetType )
				{
					atomStart = pos;
					payloadStart = pos + headerSize;
					atomEnd = currentAtomEnd;
					return true;
				}

				pos += atomSize;
			}

			return false;
		}

		private static ushort ReadUInt16BigEndian( byte[] buffer, int offset )
		{
			return ( ushort )( ( buffer[ offset ] << 8 ) | buffer[ offset + 1 ] );
		}

		private static uint ReadUInt32BigEndian( byte[] buffer, int offset )
		{
			return ( uint )( ( buffer[ offset ] << 24 )
				| ( buffer[ offset + 1 ] << 16 )
				| ( buffer[ offset + 2 ] << 8 )
				| buffer[ offset + 3 ] );
		}

		private static ulong ReadUInt64BigEndian( byte[] buffer, int offset )
		{
			return ( ( ulong )buffer[ offset ] << 56 )
				| ( ( ulong )buffer[ offset + 1 ] << 48 )
				| ( ( ulong )buffer[ offset + 2 ] << 40 )
				| ( ( ulong )buffer[ offset + 3 ] << 32 )
				| ( ( ulong )buffer[ offset + 4 ] << 24 )
				| ( ( ulong )buffer[ offset + 5 ] << 16 )
				| ( ( ulong )buffer[ offset + 6 ] << 8 )
				| buffer[ offset + 7 ];
		}

		private static void LogMp4ContainerInfo( FileStream fs )
		{
			long original = fs.Position;
			try
			{
				if( fs.Length < 16 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] File too small for MP4 diagnostics." );
					return;
				}

				fs.Position = 0;
				var header = new byte[ 16 ];
				int got = fs.Read( header, 0, header.Length );
				if( got < 8 )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] Unable to read MP4 header." );
					return;
				}

				uint atomSize = ReadUInt32BigEndian( header, 0 );
				string atomType = new string( new[] { ( char )header[ 4 ], ( char )header[ 5 ], ( char )header[ 6 ], ( char )header[ 7 ] } );
				Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] First atom='{atomType}', size={atomSize}" );

				if( atomType != "ftyp" )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "[M4AProcessor] Warning: MP4 container does not start with 'ftyp'." );
				}
				else if( got >= 16 )
				{
					string majorBrand = new string( new[] { ( char )header[ 8 ], ( char )header[ 9 ], ( char )header[ 10 ], ( char )header[ 11 ] } );
					uint minorVersion = ReadUInt32BigEndian( header, 12 );
					Debug.WriteLineIf( EnableVerboseTrace, $"[M4AProcessor] majorBrand='{majorBrand}', minorVersion=0x{minorVersion:X8}" );
				}
			}
			finally
			{
				fs.Position = original;
			}
		}
	}
}
