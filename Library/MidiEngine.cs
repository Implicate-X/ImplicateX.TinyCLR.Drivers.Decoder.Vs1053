using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public partial class Device
	{
		/// <summary>
		/// Provides MIDI transport, UART listener, and MIDI file playback support for the VS1053 codec.
		/// The class can route MIDI data through a UART port when available and fall back to SDI-based
		/// forwarding when UART transport is unavailable.
		/// </summary>
		public sealed class MidiEngine( Device device	)
		{
			/// <summary>
			/// UART transport used to send MIDI bytes to the VS1053 in real-time mode.
			/// </summary>
			private SerialPort port;

			/// <summary>
			/// Background worker that continuously reads incoming UART MIDI data.
			/// </summary>
			private Thread listenerThread;

			/// <summary>
			/// Indicates whether the UART listener loop should continue running.
			/// Marked <see langword="volatile"/> because it is shared across threads.
			/// </summary>
			private volatile bool listenerRunning;

			/// <summary>
			/// Total number of raw MIDI bytes received through UART since listener start.
			/// </summary>
			private int uartBytesReceived;

			/// <summary>
			/// Total number of MIDI messages forwarded to the device from UART input.
			/// </summary>
			private int uartMessagesForwarded;

			/// <summary>
			/// Indicates whether MIDI mode setup was completed for the current session.
			/// </summary>
			private bool modeInitialized;

			/// <summary>
			/// Indicates whether the VS1053 real-time MIDI bootstrap sequence has been attempted.
			/// </summary>
			private bool bootstrapAttempted;

			/// <summary>
			/// Indicates that the bootstrap sequence failed and hardware real-time mode is unavailable.
			/// </summary>
			private bool bootstrapFailed;

			/// <summary>
			/// Indicates that the VS1053 is currently operating in hardware real-time MIDI mode.
			/// </summary>
			private bool hardwareRealtimeMode;
			/// <summary>
			/// Initializes UART MIDI transport and applies the VS1053 real-time MIDI boot pin and reset sequence.
			/// </summary>
			/// <remarks>
			/// When <paramref name="uartControllerName"/> is null or empty, UART MIDI is disabled and the driver
			/// continues using the SDI fallback path only.
			/// </remarks>
			/// <param name="uartControllerName">TinyCLR UART controller name used for 31,250 baud MIDI traffic.</param>
			/// <param name="startListener">True to start UART receive listener; false to skip listener startup.</param>
			public void Initialize( string uartControllerName, bool startListener = true )
			{
				try
				{
					StopUartListener();
					CloseUartPort();

					if( string.IsNullOrEmpty( uartControllerName ) )
					{
						Debug.WriteLineIf( EnableVerboseTrace, "UART MIDI disabled, using SDI path." );
						return;
					}

					this.bootstrapAttempted = false;
					this.bootstrapFailed = false;
					this.hardwareRealtimeMode = false;

					this.port = new SerialPort( uartControllerName )
					{
						BaudRate = 31250,
						Parity = Parity.None,
						DataBits = 8,
						StopBits = StopBits.One,
						Handshake = Handshake.None
					};

					this.port.Open();
					Debug.WriteLineIf( EnableVerboseTrace, "UART MIDI port opened: " + uartControllerName );

					ConfigureRealtimeBootPinsAndReset();

					if( startListener )
					{
						StartUartListener();
					}
					else
					{
						Debug.WriteLineIf( EnableVerboseTrace, "UART MIDI listener disabled for this initialization." );
					}
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "Failed to initialize UART MIDI: " + ex.Message );
					StopUartListener();
					CloseUartPort();
				}
			}

			/// <summary>
			/// Configures VS1053 boot pins for real-time MIDI mode and pulses reset.
			/// </summary>
			/// <remarks>
			/// Applies GPIO0 low and GPIO1 high before reset sampling when both pins are available,
			/// which is required for hardware real-time MIDI boot mode.
			/// </remarks>
			private void CloseUartPort()
			{
				if( this.port == null )
				{
					return;
				}

				try
				{
					if( this.port.IsOpen )
					{
						this.port.Close();
					}
				}
				catch { }
				finally
				{
					this.port = null;
				}
			}

			private void ConfigureRealtimeBootPinsAndReset()
			{
				try
				{
					if( device.gpio0Pin != null && device.gpio1Pin != null )
					{
						device.gpio0Pin.SetDriveMode( GpioPinDriveMode.Output );
						device.gpio1Pin.SetDriveMode( GpioPinDriveMode.Output );
						device.gpio0Pin.Write( GpioPinValue.Low );
						device.gpio1Pin.Write( GpioPinValue.High );

						Thread.Sleep( 5 );
						Debug.WriteLineIf( EnableVerboseTrace, "GPIO0=LOW and GPIO1=HIGH applied before RESET (RT-MIDI boot mode)." );

						this.hardwareRealtimeMode = true;
						this.bootstrapAttempted = true;
						this.bootstrapFailed = false;
					}
					else
					{
						Debug.WriteLineIf( EnableVerboseTrace, "GPIO0/GPIO1 not fully available. Falling back to software RT-MIDI bootstrap if needed." );
					}

					if( device.resetPin != null )
					{
						device.resetPin.Write( GpioPinValue.Low );
						Thread.Sleep( 10 );
						device.resetPin.Write( GpioPinValue.High );
						Thread.Sleep( 20 );
						Debug.WriteLineIf( EnableVerboseTrace, "VS1053 reset pulse applied." );
					}
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "Failed to apply boot pins/reset for RT-MIDI: " + ex.Message );
				}
			}

			/// <summary>
			/// Sends a short UART MIDI melody to verify transport and VS1053 routing.
			/// </summary>
			/// <remarks>
			/// This is intended as a quick functional test for the serial MIDI path.
			/// </remarks>
			public void SendUartTestSequence()
			{
				if( this.port == null || !this.port.IsOpen )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "UART MIDI port not open for test sequence." );
					return;
				}

				int bpm = 72;
				int beatDuration = ( int )( 60.0 / bpm * 1000 );
				int noteDuration = beatDuration / 4;
				byte[] melody = [64, 66, 71, 73, 74, 66, 64, 73, 71, 66, 74, 73];

				try
				{
					this.port.Write( [0xC0, 0x00], 0, 2 );

					for( int i = 0; i < melody.Length; i++ )
					{
						byte note = melody[ i ];
						this.port.Write( [0x90, note, 127], 0, 3 );
						Thread.Sleep( noteDuration );
						this.port.Write( [0x80, note, 0], 0, 3 );
						Thread.Sleep( 10 );
					}
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "Failed to send UART test sequence: " + ex.Message );
				}
			}

			/// <summary>
			/// Sends MIDI Bank Select (Control Change 0) on the specified channel.
			/// </summary>
			/// <param name="channel">MIDI channel number in the range 0-15.</param>
			/// <param name="bank">Bank number to select.</param>
			public void BankSelect( byte channel, byte bank )
			{
				SendBytes( [( byte )( 0xB0 | ( channel & 0x0F ) ), 0x00, bank] );
			}

			/// <summary>
			/// Sends a MIDI Program Change on the specified channel.
			/// </summary>
			/// <param name="channel">MIDI channel number in the range 0-15.</param>
			/// <param name="program">Program number to select.</param>
			public void ProgramChange( byte channel, byte program )
			{
				SendBytes( [( byte )( 0xC0 | ( channel & 0x0F ) ), program] );
			}

			/// <summary>
			/// Sends a MIDI Note On message on the specified channel.
			/// </summary>
			/// <param name="channel">MIDI channel number in the range 0-15.</param>
			/// <param name="note">MIDI note number.</param>
			/// <param name="velocity">Note velocity.</param>
			public void NoteOn( byte channel, byte note, byte velocity )
			{
				SendBytes( [( byte )( 0x90 | ( channel & 0x0F ) ), note, velocity] );
			}

			/// <summary>
			/// Sends a MIDI Note Off message on the specified channel.
			/// </summary>
			/// <param name="channel">MIDI channel number in the range 0-15.</param>
			/// <param name="note">MIDI note number.</param>
			public void NoteOff( byte channel, byte note )
			{
				SendBytes( [( byte )( 0x80 | ( channel & 0x0F ) ), note, 0x00] );
			}

			/// <summary>
			/// Performs the VS1053 startup sequence required for real-time MIDI mode.
			/// </summary>
			/// <remarks>
			/// The method resets hardware, configures the codec registers, waits for DREQ to stabilize,
			/// and enables the analog path before marking MIDI mode as initialized.
			/// </remarks>
			private void EnterRealtimeMode()
			{
				if( this.modeInitialized )
				{
					return;
				}

				for( int attempt = 0; attempt < 2; attempt++ )
				{
					try
					{
						device.ResetHardware();
						device.ConfigureStartupRegisters();
						device.WaitDreqStableHigh( 3, 1000 );
						Thread.Sleep( 20 );

						device.SciWriteWithRecovery( Register.WRAMBaseAddress, 0xC017 );
						device.SciWriteWithRecovery( Register.WRAMWriteRead, 0x0000 );
						device.SciWriteWithRecovery( Register.AudioData, 44101 );
						device.SciWriteWithRecovery( Register.AppStartAddress, 0x0050 );
						Thread.Sleep( 50 );

						device.EnableAnalogPath();
						device.SetVolume( 250, 250 );

						this.modeInitialized = true;

						return;
					}
					catch( TimeoutException )
					{
						if( attempt == 1 )
						{
							throw;
						}

						Thread.Sleep( 50 );
					}
				}
			}

			/// <summary>
			/// Writes a 3-byte MIDI message to SDI packet format.
			/// </summary>
			private void WriteMessage( byte status, byte data1, byte data2 )
			{
				WritePacket( [0x00, status, data1, data2] );
			}

			/// <summary>
			/// Writes a MIDI Program Change packet to SDI packet format.
			/// </summary>
			private void WriteProgramChange( byte channel, byte program )

			{
				WritePacket( [0x00, ( byte )( 0xC0 | ( channel & 0x0F ) ), program] );
			}

			/// <summary>
			/// Writes a raw SDI packet while holding the SPI synchronization lock.
			/// </summary>
			/// <param name="packet">Packet payload to transmit to the codec.</param>
			private void WritePacket( byte[] packet )

			{
				if( packet == null )
				{
					throw new ArgumentNullException( nameof( packet ) );
				}

				lock( device.spiSync )
				{
					device.EnableSdi();

					try
					{
						device.spiDataDevice.Write( packet, 0, packet.Length );
					}
					finally
					{
						device.DisableSdi();
					}
				}
			}

			/// <summary>
			/// Sends raw MIDI bytes through UART when available, otherwise routes them to SDI.
			/// </summary>
			/// <param name="midi">Raw MIDI message bytes.</param>
			private void SendBytes( byte[] midi )
			{
				if( midi == null || midi.Length == 0 )
					return;

				try
				{
					if( this.port != null && this.port.IsOpen )
					{
						this.port.Write( midi, 0, midi.Length );
						return;
					}
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "UART MIDI write failed: " + ex.Message + " - fallback to SDI." );
				}

				RouteMidiToSdi( midi );
			}

			/// <summary>
			/// Ensures that the codec is available in real-time MIDI mode.
			/// </summary>
			/// <remarks>
			/// If hardware boot mode is already active, the method returns immediately. Otherwise it
			/// attempts the software bootstrap path once and records any failure for later calls.
			/// </remarks>
			private bool TryEnsureRealtimeMode()
			{
				if( this.hardwareRealtimeMode )
					return true;

				if( this.bootstrapFailed )
					return false;

				if( this.bootstrapAttempted )
					return true;

				try
				{
					EnterRealtimeMode();

					this.bootstrapAttempted = true;
					this.bootstrapFailed = false;
					
					return true;
				}
				catch( Exception ex )
				{
					this.bootstrapAttempted = true;
					this.bootstrapFailed = true;
					
					Debug.WriteLineIf( EnableVerboseTrace, "Realtime MIDI bootstrap failed: " + ex.Message );

					LogSciRegisters();

					return false;
				}
			}

			/// <summary>
			/// Routes a raw MIDI message to the VS1053 SDI path.
			/// </summary>
			/// <remarks>
			/// Handles short messages and program changes explicitly so they are encoded correctly for
			/// the codec's real-time MIDI packet format.
			/// </remarks>
			/// <param name="midi">Raw MIDI message bytes.</param>
			private void RouteMidiToSdi( byte[] midi )
			{
				if( midi == null || midi.Length == 0 )
					return;

				if( !TryEnsureRealtimeMode() )
					return;

				try
				{
					if( midi.Length == 1 )
					{
						WritePacket( [0x00, midi[ 0 ]] );
					}
					else if( midi.Length == 2 )
					{
						if( ( midi[ 0 ] & 0xF0 ) == 0xC0 )
						{
							byte channel = ( byte )( midi[ 0 ] & 0x0F );

							WriteProgramChange( channel, midi[ 1 ] );
						}
						else
						{
							WritePacket( [0x00, midi[ 0 ], midi[ 1 ]] );
						}
					}
					else
					{
						WriteMessage( midi[ 0 ], midi[ 1 ], midi[ 2 ] );
					}
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "Forward to SDI failed: " + ex.Message );
				}
			}

			/// <summary>
			/// Logs SCI register values to help diagnose real-time MIDI bootstrap failures.
			/// </summary>
			private void LogSciRegisters()
			{
				try
				{
					ushort mode = device.SciRead( Register.Mode );
					ushort status = device.SciRead( Register.Status );
					ushort clockFrequency = device.SciRead( Register.ClockFrequency );
					ushort volume = device.SciRead( Register.Volume );

					Debug.WriteLineIf( EnableVerboseTrace, "SCI Mode=0x" + mode.ToString( "X4" ) +
						" Status=0x" + status.ToString( "X4" ) +
						" ClockF=0x" + clockFrequency.ToString( "X4" ) +
						" Volume=0x" + volume.ToString( "X4" ) );
				}
				catch( Exception ex )
				{
					Debug.WriteLineIf( EnableVerboseTrace, "SCI register read failed: " + ex.Message );
				}
			}

			/// <summary>
			/// Starts the background UART MIDI listener.
			/// </summary>
			/// <remarks>
			/// The listener parses incoming UART MIDI bytes, handles running status, skips SysEx payloads,
			/// and forwards completed channel messages to the SDI fallback path.
			/// </remarks>
			private void StartUartListener()
			{
				StopUartListener();

				if( this.port == null || !this.port.IsOpen )
					return;

				this.uartBytesReceived = 0;
				this.uartMessagesForwarded = 0;
				this.listenerRunning = true;

				this.listenerThread = new Thread( () =>
				{
					byte currentStatus = 0;
					int needed = 0;
					byte[] dataBuf = new byte[ 2 ];
					int dataIdx = 0;
					SerialPort localPort = this.port;

					while( this.listenerRunning && localPort != null && localPort.IsOpen )
					{
						try
						{
							int v = localPort.ReadByte();

							if( v < 0 )
							{
								Thread.Sleep( 1 );
								continue;
							}

							byte b = ( byte )v;
							this.uartBytesReceived++;

							if( ( this.uartBytesReceived & 0x3F ) == 0 )
							{
								Debug.WriteLineIf( EnableVerboseTrace, "UART bytes received: " + this.uartBytesReceived +
									", messages forwarded: " + this.uartMessagesForwarded );
							}

							if( b == 0xF0 )
							{
								int sysExByteCount = 1;

								while( true )
								{
									int vb = localPort.ReadByte();

									if( vb < 0 ) break;

									sysExByteCount++;

									if( vb == 0xF7 ) break;
								}
								Debug.WriteLineIf( EnableVerboseTrace, "Received SysEx bytes: " + sysExByteCount );
								continue;
							}

							if( ( b & 0x80 ) != 0 )
							{
								currentStatus = b;
								dataIdx = 0;
								int code = currentStatus & 0xF0;

								needed = code switch
								{
									0xC0 or 0xD0 => 1,
									0xF0 => 0,
									_ => 2,
								};

								if( needed == 0 )
								{
									RouteMidiToSdi( [currentStatus] );
									this.uartMessagesForwarded++;
								}
							}
							else
							{
								if( currentStatus == 0 )
									continue;

								dataBuf[ dataIdx++ ] = b;

								if( dataIdx >= needed )
								{
									if( needed == 1 )
									{
										RouteMidiToSdi( [currentStatus, dataBuf[ 0 ]] );
									}
									else
									{
										RouteMidiToSdi( [currentStatus, dataBuf[ 0 ], dataBuf[ 1 ]] );
									}
									this.uartMessagesForwarded++;
									dataIdx = 0;
								}
							}
						}
						catch( Exception ex )
						{
							Debug.WriteLineIf( EnableVerboseTrace, "MIDI UART listener error: " + ex.Message );
							Thread.Sleep( 10 );
						}
					}
				} );

				this.listenerThread.Start();
			}

			/// <summary>
			/// Stops the background UART listener.
			/// </summary>
			private void StopUartListener()
			{
				this.listenerRunning = false;
				try
				{
					if( this.listenerThread != null )
					{
						this.listenerThread.Join( 500 );
					}
				}
				catch { }
				this.listenerThread = null;
			}


			/// <summary>
			/// Runs a MIDI stress test covering chords, arpeggios, percussion, program changes, and bursts.
			/// </summary>
			public void RunStressTest()
			{
				BankSelect( 0, 1 );   // Bank 1 for channel 0
									  // Base setup: Instruments on channels 0–3, Drums on 9
				ProgramChange( 0, 0 );   // Piano
				ProgramChange( 1, 40 );  // Violin
				ProgramChange( 2, 81 );  // Lead 1 (Square)
				ProgramChange( 3, 89 );  // Pad 2 (Warm)
										 // Channel 9: GM Drumset automatically

				int tempoMs = 120; // Base beat for fast sequences

				// 1. Polyphonic chord block – all 4 channels simultaneously
				for( int i = 0; i < 8; i++ )
				{
					// C major chord on 4 channels
					NoteOn( 0, 60, 120 ); // C4
					NoteOn( 1, 64, 110 ); // E4
					NoteOn( 2, 67, 110 ); // G4
					NoteOn( 3, 72, 100 ); // C5

					Thread.Sleep( tempoMs );
					
					NoteOff( 0, 60 );
					NoteOff( 1, 64 );
					NoteOff( 2, 67 );
					NoteOff( 3, 72 );
					
					Thread.Sleep( tempoMs / 2 );
				}

				// 2. Fast arpeggios on channel 2 (Lead) + simultaneous pads on channel 3
				byte[] arp = [60, 64, 67, 72];

				for( int i = 0; i < 64; i++ )
				{
					byte n = arp[ i % arp.Length ];

					// Lead-Arpeggio
					NoteOn( 2, n, 120 );

					// Pad holds root note
					switch( i % 8 )
					{
						case 0: NoteOn( 3, 48, 80 ); break; // C3
						case 4: NoteOff( 3, 48 ); break; // C3
					}

					Thread.Sleep( tempoMs / 2 );

					NoteOff( 2, n );
				}

				// 3. Percussion pattern on channel 9 – Kick, Snare, HiHat
				for( int i = 0; i < 64; i++ )
				{
					// Kick
					NoteOn( 9, 36, 120 ); Thread.Sleep( 40 ); NoteOff( 9, 36 );

					// Snare on offbeats
					if( ( i & 3 ) == 2 )
					{
						NoteOn( 9, 38, 110 ); Thread.Sleep( 40 ); NoteOff( 9, 38 );
					}

					// HiHat every unit
					NoteOn( 9, 42, 70 ); Thread.Sleep( 40 ); NoteOff( 9, 42 );

					Thread.Sleep( tempoMs / 2 );
				}

				// 4. Channel change and ProgramChange spam – Stress for the synth engine
				for( int p = 0; p < 16; p++ )
				{
					ProgramChange( 0, ( byte )p );
					ProgramChange( 1, ( byte )( p + 16 ) );
					ProgramChange( 2, ( byte )( p + 32 ) );
					ProgramChange( 3, ( byte )( p + 48 ) );

					// Short cluster chords
					NoteOn( 0, 60, 100 );
					NoteOn( 1, 61, 100 );
					NoteOn( 2, 62, 100 );
					NoteOn( 3, 63, 100 );
					Thread.Sleep( tempoMs / 2 );
					NoteOff( 0, 60 );
					NoteOff( 1, 61 );
					NoteOff( 2, 62 );
					NoteOff( 3, 63 );

					Thread.Sleep( tempoMs / 2 );
				}

				// 5. Fast NoteOn/NoteOff bursts – Timing and polyphony stress
				for( int i = 0; i < 128; i++ )
				{
					byte ch = ( byte )( i % 4 );
					byte note = ( byte )( 60 + ( i % 12 ) ); // small scale

					NoteOn( ch, note, 100 );
					Thread.Sleep( 30 );
					NoteOff( ch, note );
					Thread.Sleep( 30 );
				}

				// 6. Conclusion: large, multi-voice final chord
				ProgramChange( 0, 0 );
				ProgramChange( 1, 40 );
				ProgramChange( 2, 81 );
				ProgramChange( 3, 89 );

				NoteOn( 0, 60, 120 );
				NoteOn( 1, 64, 110 );
				NoteOn( 2, 67, 110 );
				NoteOn( 3, 72, 100 );
				NoteOn( 9, 36, 120 ); // Kick added
				Thread.Sleep( 1500 );
				NoteOff( 0, 60 );
				NoteOff( 1, 64 );
				NoteOff( 2, 67 );
				NoteOff( 3, 72 );
				NoteOff( 9, 36 );
			}

			/// <summary>
			/// Plays a Standard MIDI File (SMF) by parsing its tracks and dispatching supported events.
			/// </summary>
			/// <remarks>
			/// Supports tempo meta events, channel messages, running status, and basic SysEx skipping.
			/// The optional <paramref name="maxDeltaMs"/> value limits the maximum sleep interval between
			/// events, and <paramref name="playbackStatus"/> receives progress updates.
			/// </remarks>
			/// <param name="filePath">Path to the MIDI file.</param>
			/// <param name="maxDeltaMs">Optional maximum delay applied between events, or 0 for no cap.</param>
			/// <param name="playbackStatus">Optional callback for playback progress messages.</param>
			public bool PlayFile( string filePath, int maxDeltaMs = 0, Action<string> playbackStatus = null )
			{
				playbackStatus?.Invoke( "Preparing MIDI playback..." );

				try
				{
					if( !File.Exists( filePath ) )
					{
						throw new Exception( "MIDI file not found." );
					}

					byte[] data = File.ReadAllBytes( filePath );

					if( data.Length < 14 || Encoding.UTF8.GetString( data, 0, 4 ) != "MThd" )
					{
						throw new Exception( "Invalid MIDI header." );
					}

					int headerLength = ReadInt32( data, 4 );
					int trackCount = ReadInt16( data, 10 );
					int division = ReadInt16( data, 12 );

					if( division <= 0 )
					{
						throw new Exception( "Unsupported MIDI division value." );
					}

					if( trackCount <= 0 )
					{
						playbackStatus?.Invoke( "No tracks to play." );
						return false;
					}

					int pos = 8 + headerLength;
					if( pos < 0 || pos > data.Length )
					{
						throw new Exception( "Invalid MIDI header length." );
					}

					TrackState[] trackStates = new TrackState[ trackCount ];
					TrackEvent[] nextEvents = new TrackEvent[ trackCount ];

					bool[] hasEvent = new bool[ trackCount ];

					for( int t = 0; t < trackCount; t++ )
					{
						if( pos + 8 > data.Length || Encoding.UTF8.GetString( data, pos, 4 ) != "MTrk" )
						{
							throw new Exception( "Track chunk missing." );
						}

						int trackLength = ReadInt32( data, pos + 4 );
						int trackPos = pos + 8;
						int trackEnd = trackPos + trackLength;
						if( trackLength < 0 || trackEnd > data.Length )
						{
							throw new Exception( "Invalid track length." );
						}

						trackStates[ t ] = new TrackState
						{
							TrackPos = trackPos,
							TrackEnd = trackEnd,
							RunningStatus = 0,
							AbsoluteTick = 0,
							Finished = false
						};

						hasEvent[ t ] = TryReadNextEvent( data, ref trackStates[ t ], out nextEvents[ t ] );
						pos = trackEnd;
					}

					int tempoUsPerQuarter = 500000;
					long currentTick = 0;
					bool started = false;

					while( true )
					{
						int nextTrack = -1;
						for( int t = 0; t < trackCount; t++ )
						{
							if( !hasEvent[ t ] )
							{
								continue;
							}

							if( nextTrack < 0 )
							{
								nextTrack = t;
								continue;
							}

							TrackEvent candidate = nextEvents[ t ];
							TrackEvent current = nextEvents[ nextTrack ];

							if( candidate.Tick < current.Tick || ( candidate.Tick == current.Tick && candidate.IsTempo && !current.IsTempo ) )
							{
								nextTrack = t;
							}
						}

						if( nextTrack < 0 )
						{
							break;
						}

						TrackEvent e = nextEvents[ nextTrack ];
						long deltaTicks = e.Tick - currentTick;

						if( deltaTicks > 0 )
						{
							int delayMs = ConvertDeltaToMs( deltaTicks, division, tempoUsPerQuarter, maxDeltaMs );
							if( !started && !e.IsTempo && delayMs > 0 )
							{
								playbackStatus?.Invoke( "Waiting for first note: " + delayMs + " ms" );
							}

							Thread.Sleep( delayMs );
							currentTick = e.Tick;
						}

						if( e.IsTempo )
						{
							tempoUsPerQuarter = e.TempoUsPerQuarter;
						}
						else
						{
							if( !started )
							{
								started = true;
								playbackStatus?.Invoke( "Playback started." );
							}

							DispatchMidiEvent( e );
						}

						hasEvent[ nextTrack ] = TryReadNextEvent( data, ref trackStates[ nextTrack ], out nextEvents[ nextTrack ] );
					}

					playbackStatus?.Invoke( "Playback completed." );
				}
				catch( Exception ex )
				{
					playbackStatus?.Invoke( "Playback failed: " + ex.Message );
					return false;
				}

				return true;
			}

			/// <summary>
			/// Plays a MIDI file and restores the decoder to standard audio mode afterwards.
			/// </summary>
			/// <param name="filePath">Path to the MIDI file.</param>
			/// <param name="maxDeltaMs">Optional maximum delay applied between events, or 0 for no cap.</param>
			/// <param name="playbackStatus">Optional callback for playback progress messages.</param>
			public bool PlayFileAndRestoreDecodeMode( string filePath, int maxDeltaMs = 0, Action<string> playbackStatus = null )
			{
				try
				{
					return PlayFile( filePath, maxDeltaMs, playbackStatus );
				}
				finally
				{
					RestoreDecodeMode();
				}
			}

			private void RestoreDecodeMode()
			{
				try
				{
					StopUartListener();
					CloseUartPort();

					if( device.gpio0Pin != null && device.gpio1Pin != null )
					{
						device.gpio0Pin.SetDriveMode( GpioPinDriveMode.Output );
						device.gpio1Pin.SetDriveMode( GpioPinDriveMode.Output );
						device.gpio0Pin.Write( GpioPinValue.Low );
						device.gpio1Pin.Write( GpioPinValue.Low );
						Thread.Sleep( 1 );
						device.gpio0Pin.SetDriveMode( GpioPinDriveMode.InputPullDown );
						device.gpio1Pin.SetDriveMode( GpioPinDriveMode.InputPullDown );
					}

					device.Initialize();
				}
				finally
				{
					this.modeInitialized = false;
					this.bootstrapAttempted = false;
					this.bootstrapFailed = false;
					this.hardwareRealtimeMode = false;
				}
			}

			/// <summary>
			/// Reads the next parsable MIDI event from a track state.
			/// </summary>
			/// <remarks>
			/// Tempo meta events are returned as synthetic tempo events; unsupported meta, SysEx,
			/// and system messages are skipped.
			/// </remarks>
			private bool TryReadNextEvent( byte[] data, ref TrackState state, out TrackEvent trackEvent )
			{
				trackEvent = default;

				if( state.Finished || state.TrackPos >= state.TrackEnd )
				{
					state.Finished = true;
					return false;
				}

				while( state.TrackPos < state.TrackEnd )
				{
					int delta = ReadVariableLength( data, ref state.TrackPos );
					state.AbsoluteTick += delta;

					if( state.TrackPos >= state.TrackEnd )
					{
						state.Finished = true;
						return false;
					}

					byte status = data[ state.TrackPos ];
					if( ( status & 0x80 ) != 0 )
					{
						state.TrackPos++;
						state.RunningStatus = status;
					}
					else
					{
						if( state.RunningStatus == 0 )
						{
							throw new Exception( "Invalid running status in MIDI stream." );
						}
						status = state.RunningStatus;
					}

					if( status == 0xFF )
					{
						if( state.TrackPos >= state.TrackEnd )
						{
							state.Finished = true;
							return false;
						}

						byte metaType = data[ state.TrackPos++ ];
						int len = ReadVariableLength( data, ref state.TrackPos );

						if( len < 0 || state.TrackPos + len > state.TrackEnd )
						{
							throw new Exception( "Invalid MIDI meta event length." );
						}

						if( metaType == 0x51 && len == 3 )
						{
							int tempo = ( data[ state.TrackPos ] << 16 ) | ( data[ state.TrackPos + 1 ] << 8 ) | data[ state.TrackPos + 2 ];
							state.TrackPos += len;

							trackEvent = TrackEvent.CreateTempo( state.AbsoluteTick, tempo );

							return true;
						}

						state.TrackPos += len;
						continue;
					}

					if( status == 0xF0 || status == 0xF7 )
					{
						int len = ReadVariableLength( data, ref state.TrackPos );

						if( len < 0 || state.TrackPos + len > state.TrackEnd )
						{
							throw new Exception( "Invalid MIDI SysEx event length." );
						}

						state.TrackPos += len;
						continue;
					}

					if( status >= 0xF1 )
					{
						int systemLength = GetSystemMessageDataLength( status );

						if( state.TrackPos + systemLength > state.TrackEnd )
						{
							state.Finished = true;
							return false;
						}

						state.TrackPos += systemLength;
						continue;
					}

					int dataLength = GetChannelMessageDataLength( status );

					if( dataLength == 0 || state.TrackPos + dataLength > state.TrackEnd )
					{
						state.Finished = true;
						return false;
					}

					byte data1 = data[ state.TrackPos++ ];
					byte data2 = dataLength == 2 ? data[ state.TrackPos++ ] : ( byte )0;

					trackEvent = TrackEvent.CreateChannel( state.AbsoluteTick, status, data1, data2, dataLength );

					return true;
				}

				state.Finished = true;
				return false;
			}

			/// <summary>
			/// Dispatches a parsed MIDI event to the appropriate MIDI output method.
			/// </summary>
			private void DispatchMidiEvent( TrackEvent e )
			{
				byte eventType = ( byte )( e.Status & 0xF0 );
				byte channel = ( byte )( e.Status & 0x0F );

				switch( eventType )
				{
					case 0x80:
						NoteOff( channel, e.Data1 );
						break;

					case 0x90:
						if( e.Data2 == 0 )
							NoteOff( channel, e.Data1 );
						else
							NoteOn( channel, e.Data1, e.Data2 );
						break;

					case 0xB0:
						SendBytes( [e.Status, e.Data1, e.Data2] );
						break;

					case 0xC0:
						ProgramChange( channel, e.Data1 );
						break;

					default:
						switch ( e.DataLength )
						{
							case 0:	SendBytes( [ e.Status ] ); break;
							case 1:	SendBytes( [ e.Status, e.Data1 ] ); break;
							case 2:	SendBytes( [ e.Status, e.Data1, e.Data2 ] ); break;
						}
						break;
				}
			}

			/// <summary>
			/// Returns the number of data bytes expected for a MIDI channel message.
			/// </summary>
			private int GetChannelMessageDataLength( byte status )
			{
				return ( status & 0xF0 ) switch
				{
					0xC0 or 0xD0 => 1,
					0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 2,
					_ => 0,
				};
			}

			/// <summary>
			/// Returns the number of data bytes expected for a MIDI system message.
			/// </summary>
			private int GetSystemMessageDataLength( byte status )
			{
				return status switch
				{
					0xF1 or 0xF3 => 1,
					0xF2 => 2,
					_ => 0,
				};
			}

			/// <summary>
			/// Tracks parsing progress for a single MIDI track during file playback.
			/// </summary>
			private struct TrackState
			{
				public int TrackPos;
				public int TrackEnd;
				public byte RunningStatus;
				public long AbsoluteTick;
				public bool Finished;
			}

			/// <summary>
			/// Represents a parsed MIDI event or tempo change used by the playback scheduler.
			/// </summary>
			private struct TrackEvent
			{
				public long Tick;
				public bool IsTempo;
				public int TempoUsPerQuarter;
				public byte Status;
				public byte Data1;
				public byte Data2;
				public int DataLength;

				/// <summary>
				/// Creates a tempo change event at the specified tick.
				/// </summary>
				/// <param name="tick">The absolute tick position of the tempo change.</param>
				/// <param name="tempoUsPerQuarter">The new tempo in microseconds per quarter note.</param>
				/// <returns>A <see cref="TrackEvent"/> configured as a tempo change.</returns>
				public static TrackEvent CreateTempo( long tick, int tempoUsPerQuarter )
				{
					return new TrackEvent
					{
						Tick = tick,
						IsTempo = true,
						TempoUsPerQuarter = tempoUsPerQuarter
					};
				}

				/// <summary>
				/// Creates a MIDI channel event at the specified tick.
				/// </summary>
				/// <param name="tick">The absolute tick position of the event.</param>
				/// <param name="status">The MIDI status byte for the event.</param>
				/// <param name="data1">The first data byte.</param>
				/// <param name="data2">The second data byte.</param>
				/// <param name="dataLength">The number of valid data bytes carried by the event.</param>
				/// <returns>A <see cref="TrackEvent"/> configured as a channel event.</returns>
				public static TrackEvent CreateChannel( long tick, byte status, byte data1, byte data2, int dataLength )
				{
					return new TrackEvent
					{
						Tick = tick,
						IsTempo = false,
						Status = status,
						Data1 = data1,
						Data2 = data2,
						DataLength = dataLength
					};
				}
			}

			/// <summary>
			/// Reads a big-endian 16-bit integer from the specified byte array.
			/// </summary>
			private int ReadInt16( byte[] d, int p ) =>
				( d[ p ] << 8 ) | d[ p + 1 ];

			/// <summary>
			/// Reads a big-endian 32-bit integer from the specified byte array.
			/// </summary>
			private int ReadInt32( byte[] d, int p ) =>
				( d[ p ] << 24 ) | ( d[ p + 1 ] << 16 ) | ( d[ p + 2 ] << 8 ) | d[ p + 3 ];

			/// <summary>
			/// Reads a MIDI variable-length quantity and advances the input position.
			/// </summary>
			private int ReadVariableLength( byte[] d, ref int p )
			{
				int value = 0;
				int bytesRead = 0;
				byte b;

				do
				{
					if( p >= d.Length )
					{
						throw new Exception( "Unexpected end of MIDI data while reading variable-length value." );
					}

					if( bytesRead >= 4 )
					{
						throw new Exception( "Invalid MIDI variable-length value." );
					}

					b = d[ p++ ];
					value = ( value << 7 ) | ( b & 0x7F );
					bytesRead++;
				}
				while( ( b & 0x80 ) != 0 );

				return value;
			}

			/// <summary>
			/// Converts a MIDI delta tick value into milliseconds using the current tempo and division.
			/// </summary>
			/// <param name="delta">Delta tick count.</param>
			/// <param name="division">Ticks per quarter note.</param>
			/// <param name="tempoUsPerQuarter">Current tempo in microseconds per quarter note.</param>
			/// <param name="maxDeltaMs">Optional upper bound for the returned delay.</param>
			private int ConvertDeltaToMs( long delta, int division, int tempoUsPerQuarter, int maxDeltaMs )
			{
				double usPerTick = ( double )tempoUsPerQuarter / division;
				double ms = ( usPerTick * delta ) / 1000.0;

				if( ms <= 0 )
				{
					return 0;
				}

				if( ms > int.MaxValue )
				{
					return int.MaxValue;
				}

				int result = ( int )ms;

				if( maxDeltaMs > 0 && result > maxDeltaMs )
				{
					return maxDeltaMs;
				}

				return result;
			}
		}
	}
}
