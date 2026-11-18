using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	public partial class Vs1053
	{
		/// <summary>
		/// Prepares the decoder for continuous streaming input.
		/// </summary>
		/// <param name="loadStandardCodecPatch">Whether to load the standard codec patch before streaming.</param>
		public void StartStream( bool loadStandardCodecPatch = true )
		{
			if( loadStandardCodecPatch )
			{
				this.patchEngine.LoadPlugin( PatchEngine.PatchType.StandardCodec );
			}

			ConfigureStartupRegisters();

			this.spiDataDevice.ConnectionSettings.ClockFrequency = this.currentDataSpiFrequency;
			
			StartSong( applyStartupMode: true, fillByte: EndFillByte );
			
			this.isStreaming = true;
		}

		/// <summary>
		/// Sends one stream chunk to the decoder.
		/// </summary>
		/// <param name="buffer">Chunk buffer.</param>
		/// <param name="offset">Start offset inside <paramref name="buffer"/>.</param>
		/// <param name="count">Number of bytes to send.</param>
		public void WriteStreamChunk( byte[] buffer, int offset, int count )
		{
			if( !this.isStreaming || buffer == null || count <= 0 )
			{
				return;
			}

			SdiWriteChunks( buffer, offset, count );
		}

		/// <summary>
		/// Stops active stream playback and flushes the decoder.
		/// </summary>
		public void StopStream()
		{
			if( !this.isStreaming )
			{
				return;
			}

			StopSong();

			this.isStreaming = false;
		}

		/// <summary>
		/// Starts buffered playback from a file on the device's filesystem.
		/// </summary>
		/// <param name="filePath">Path of the audio file to stream.</param>
		public void StartFileStream( string filePath )
		{
			if( string.IsNullOrEmpty( filePath ) )
			{
				throw new ArgumentException( "A file path is required.", nameof( filePath ) );
			}

			lock( this.fileAudioControlSync )
			{
				if( this.isFileAudioStreaming )
				{
					return;
				}

				if( !File.Exists( filePath ) )
				{
					Debug.WriteLine( $"Buffered audio file not found: {filePath}" );
					return;
				}

				StartBufferedAudioWorkers( () => FileAudioProducerWorker( filePath ), null!, loadStandardCodecPatch: true );
			}
		}

		/// <summary>
		/// Starts buffered playback from a cancellable source. The codec owns the source after this call succeeds.
		/// </summary>
		/// <param name="source">The source providing audio data.</param>
		/// <param name="loadStandardCodecPatch">Whether to load the standard codec patch before streaming.</param>
		public void StartBufferedStream( IBufferedAudioSource source, bool loadStandardCodecPatch = true )
		{
			if( source == null )
			{
				throw new ArgumentNullException( nameof( source ) );
			}

			lock( this.fileAudioControlSync )
			{
				if( this.isFileAudioStreaming )
				{
					throw new InvalidOperationException( "Buffered audio playback is already active or still stopping." );
				}

				StartBufferedAudioWorkers( () => BufferedAudioProducerWorker( source ), source, loadStandardCodecPatch );
			}
		}

		/// <summary>
		/// Starts the buffered audio worker threads.
		/// </summary>
		/// <param name="producer">The producer thread start delegate.</param>
		/// <param name="source">The buffered audio source.</param>
		/// <param name="loadStandardCodecPatch">Whether to load the standard codec patch before streaming.</param>
		private void StartBufferedAudioWorkers( ThreadStart producer, IBufferedAudioSource source, bool loadStandardCodecPatch )
		{
			ResetFileAudioBuffer();

			this.stopFileAudioRequested = false;
			this.fileAudioProducerCompleted = false;
			this.fileAudioConsumerCompleted = false;
			this.isFileAudioStreaming = true;
			this.bufferedAudioSource = source;

			this.fileAudioProducerThread = new Thread( producer )
			{
				Priority = ThreadPriority.Highest
			};

			this.fileAudioConsumerThread = new Thread( () => FileAudioConsumerWorker( loadStandardCodecPatch ) )
			{
				Priority = ThreadPriority.AboveNormal
			};

			this.fileAudioProducerThread.Start();
			this.fileAudioConsumerThread.Start();
		}

		/// <summary>
		/// Stops buffered file playback and waits for its workers to finish.
		/// </summary>
		public void StopFileStream()
		{
			StopBufferedStream();
		}

		/// <summary>
		/// Stops buffered playback and waits for its workers to finish.
		/// </summary>
		public void StopBufferedStream()
		{
			Thread producerThread;
			Thread consumerThread;

			IBufferedAudioSource source;

			lock( this.fileAudioControlSync )
			{
				if( !this.isFileAudioStreaming )
				{
					return;
				}

				this.stopFileAudioRequested = true;

				producerThread = this.fileAudioProducerThread;
				consumerThread = this.fileAudioConsumerThread;

				source = this.bufferedAudioSource;
			}

			source?.Cancel();

			bool producerStopped = producerThread == null || producerThread.Join( 1500 );
			bool consumerStopped = consumerThread == null || consumerThread.Join( 2000 );

			if( !producerStopped || !consumerStopped )
			{
				throw new TimeoutException( "Timed out while stopping buffered VS1053 playback." );
			}
		}

		/// <summary>
		/// The worker thread method for producing buffered audio data.
		/// </summary>
		/// <param name="source">The buffered audio source.</param>
		private void BufferedAudioProducerWorker( IBufferedAudioSource source )
		{
			byte[] readBuffer = new byte[ FileAudioReadChunkSize ];

			try
			{
				using Stream sourceStream = source.Open();

				while( !this.stopFileAudioRequested )
				{
					int read = sourceStream.Read( readBuffer, 0, readBuffer.Length );

					if( read <= 0 )
					{
						if( !this.stopFileAudioRequested )
						{
							Debug.WriteLine( "Buffered audio source ended." );
						}
						break;
					}

					WriteToFileAudioBuffer( readBuffer, read );
				}
			}
			catch( Exception ex )
			{
				if( !this.stopFileAudioRequested )
				{
					Debug.WriteLine( $"Buffered audio producer error: {ex}" );
				}
			}
			finally
			{
				try
				{
					source.Dispose();
				}
				catch( Exception ex )
				{
					Debug.WriteLine( $"Error disposing buffered audio source: {ex}" );
				}

				this.fileAudioProducerCompleted = true;

				UpdateFileAudioStreamingState();
			}
		}

		/// <summary>
		/// The worker thread method for producing file audio data.
		/// </summary>
		/// <param name="filePath">The path to the audio file.</param>
		private void FileAudioProducerWorker( string filePath )
		{
			byte[] readBuffer = new byte[ FileAudioReadChunkSize ];

			try
			{
				using Stream sourceStream = File.OpenRead( filePath );

				SkipId3v2TagIfPresent( sourceStream, filePath );

				while( !this.stopFileAudioRequested )
				{
					int read = sourceStream.Read( readBuffer, 0, readBuffer.Length );

					if( read <= 0 )
					{
						break;
					}

					WriteToFileAudioBuffer( readBuffer, read );
				}
			}
			catch( Exception ex )
			{
				if( !this.stopFileAudioRequested )
				{
					Debug.WriteLine( $"File audio producer error: {ex.Message}" );
				}
			}
			finally
			{
				this.fileAudioProducerCompleted = true;

				UpdateFileAudioStreamingState();
			}
		}

		/// <summary>
		/// The worker thread method for consuming file audio data.
		/// </summary>
		/// <param name="loadStandardCodecPatch">Whether to load the standard codec patch before streaming.</param>
		private void FileAudioConsumerWorker( bool loadStandardCodecPatch )
		{
			byte[] consumeBuffer = new byte[ FileAudioWriteChunkSize ];

			bool streamStarted = false;
			bool initialPrefillSatisfied = false;

			try
			{
				StartStream( loadStandardCodecPatch );

				streamStarted = true;

				while( !this.stopFileAudioRequested )
				{
					int bufferedBytes = GetFileAudioBufferedBytes();

					if( !initialPrefillSatisfied )
					{
						if( bufferedBytes >= FileAudioPrefillBytes || this.fileAudioProducerCompleted )
						{
							initialPrefillSatisfied = true;
						}
						else
						{
							Thread.Sleep( 1 );
							continue;
						}
					}

					int bytesRead = ReadFromFileAudioBuffer( consumeBuffer, consumeBuffer.Length );

					if( bytesRead > 0 )
					{
						WriteStreamChunk( consumeBuffer, 0, bytesRead );
						continue;
					}

					if( this.fileAudioProducerCompleted )
					{
						break;
					}

					Thread.Sleep( 1 );
				}
			}
			catch( Exception ex )
			{
				this.stopFileAudioRequested = true;
				
				Debug.WriteLine( $"Buffered audio consumer error: {ex.Message}" );

				try
				{
					this.bufferedAudioSource?.Cancel();
				}
				catch( Exception cancelError )
				{
					Debug.WriteLine( $"Error cancelling buffered audio source: {cancelError}" );
				}
			}
			finally
			{
				if( streamStarted )
				{
					try
					{
						StopStream();
					}
					catch( Exception ex )
					{
						Debug.WriteLine( $"Error stopping VS1053 file stream: {ex.Message}" );
					}
				}

				this.fileAudioConsumerCompleted = true;

				UpdateFileAudioStreamingState();
			}
		}

		/// <summary>
		/// Skips the ID3v2 tag in an MP3 file if present.
		/// </summary>
		/// <param name="sourceStream">The source stream of the MP3 file.</param>
		/// <param name="filePath">The path to the MP3 file.</param>
		private void SkipId3v2TagIfPresent( Stream sourceStream, string filePath )
		{
			if( !sourceStream.CanSeek || Path.GetExtension( filePath ).ToLower() != ".mp3" )
			{
				return;
			}

			long originalPosition = sourceStream.Position;
			byte[] header = new byte[ 10 ];

			int read = sourceStream.Read( header, 0, header.Length );

			if( read < header.Length || header[ 0 ] != ( byte )'I' || header[ 1 ] != ( byte )'D' || header[ 2 ] != ( byte )'3' )
			{
				sourceStream.Position = originalPosition;
				return;
			}

			int tagSize = ( header[ 6 ] << 21 ) | ( header[ 7 ] << 14 ) | ( header[ 8 ] << 7 ) | header[ 9 ];
			long payloadStart = originalPosition + 10 + tagSize;

			sourceStream.Position = payloadStart < sourceStream.Length ? payloadStart : originalPosition;
		}

		/// <summary>
		/// Writes audio data to the file audio buffer.
		/// </summary>
		/// <param name="source">The source audio data.</param>
		/// <param name="count">The number of bytes to write.</param>
		private void WriteToFileAudioBuffer( byte[] source, int count )
		{
			int offset = 0;

			while( offset < count && !this.stopFileAudioRequested )
			{
				int written = 0;

				lock( this.fileAudioBufferSync )
				{
					int freeSpace = this.fileAudioRingBuffer.Length - this.fileAudioBufferedBytes;

					if( freeSpace > 0 )
					{
						int writeCount = Math.Min( count - offset, freeSpace );
						int firstPart = Math.Min( writeCount, this.fileAudioRingBuffer.Length - this.fileAudioWritePosition );

						Array.Copy( source, offset, this.fileAudioRingBuffer, this.fileAudioWritePosition, firstPart );

						this.fileAudioWritePosition = ( this.fileAudioWritePosition + firstPart ) % this.fileAudioRingBuffer.Length;
						this.fileAudioBufferedBytes += firstPart;

						offset += firstPart;
						written += firstPart;

						int secondPart = writeCount - firstPart;

						if( secondPart > 0 )
						{
							Array.Copy( source, offset, this.fileAudioRingBuffer, this.fileAudioWritePosition, secondPart );

							this.fileAudioWritePosition = ( this.fileAudioWritePosition + secondPart ) % this.fileAudioRingBuffer.Length;
							this.fileAudioBufferedBytes += secondPart;

							offset += secondPart;
							written += secondPart;
						}
					}
				}

				if( written == 0 )
				{
					Thread.Sleep( 1 );
				}
			}
		}

		/// <summary>
		/// Reads audio data from the file audio buffer.
		/// </summary>
		/// <param name="destination">The destination buffer.</param>
		/// <param name="maxCount">The maximum number of bytes to read.</param>
		/// <returns>The number of bytes read.</returns>
		private int ReadFromFileAudioBuffer( byte[] destination, int maxCount )
		{
			lock( this.fileAudioBufferSync )
			{
				int readCount = Math.Min( maxCount, this.fileAudioBufferedBytes );

				if( readCount <= 0 )
				{
					return 0;
				}

				int firstPart = Math.Min( readCount, this.fileAudioRingBuffer.Length - this.fileAudioReadPosition );

				Array.Copy( this.fileAudioRingBuffer, this.fileAudioReadPosition, destination, 0, firstPart );

				this.fileAudioReadPosition = ( this.fileAudioReadPosition + firstPart ) % this.fileAudioRingBuffer.Length;
				this.fileAudioBufferedBytes -= firstPart;

				int secondPart = readCount - firstPart;
				if( secondPart > 0 )
				{
					Array.Copy( this.fileAudioRingBuffer, this.fileAudioReadPosition, destination, firstPart, secondPart );

					this.fileAudioReadPosition = ( this.fileAudioReadPosition + secondPart ) % this.fileAudioRingBuffer.Length;
					this.fileAudioBufferedBytes -= secondPart;
				}

				return readCount;
			}
		}

		/// <summary>
		/// Gets the number of bytes currently buffered in the file audio buffer.
		/// </summary>
		/// <returns>The number of bytes buffered.</returns>
		private int GetFileAudioBufferedBytes()
		{
			lock( this.fileAudioBufferSync )
			{
				return this.fileAudioBufferedBytes;
			}
		}

		/// <summary>
		/// Resets the file audio buffer.
		/// </summary>
		private void ResetFileAudioBuffer()
		{
			lock( this.fileAudioBufferSync )
			{
				this.fileAudioWritePosition = 0;
				this.fileAudioReadPosition = 0;
				this.fileAudioBufferedBytes = 0;
			}
		}

		/// <summary>
		/// Updates the file audio streaming state.
		/// </summary>
		private void UpdateFileAudioStreamingState()
		{
			if( this.fileAudioProducerCompleted && this.fileAudioConsumerCompleted )
			{
				this.isFileAudioStreaming = false;
			}
		}
	}
}
