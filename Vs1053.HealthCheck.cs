using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using GHIElectronics.TinyCLR.Devices.Gpio;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	public partial class Vs1053
	{
		/// <summary>
		/// Reads one or more SCI registers and writes a formatted dump to the debug output.
		/// </summary>
		/// <param name="context">Caller-provided context prefix for the debug output.</param>
		/// <param name="registers">Registers to read and log.</param>
		public void DumpSciRegisters()
		{
			string dreq = this.dreqPin.Read() == GpioPinValue.High ? "HIGH" : "LOW";
			string output = $"DREQ={dreq}";

			for( int i = 0; i < 16; i++ )
			{
				Register register = ( Register )i;
				ushort value = SciRead( register );
				output += $", SCI_{register}=0x{value:X4}";
			}

			Debug.WriteLine( output );
		}

		/// <summary>
		/// Executes a deterministic VS1053 health check sequence and returns a per-test result code.
		/// </summary>
		/// <param name="includeSineTest"><see langword="true"/> to include the audible sine startup test.</param>
		/// <param name="sineDurationMs">Sine test duration in milliseconds when enabled.</param>
		/// <returns>A report with one return code per test and an overall health state.</returns>
		public HealthCheckReport CheckHealth( bool includeSineTest = false, int sineDurationMs = 2000 )
		{
			var results = new List<HealthTestResult>();
			ushort originalVolume = 0;
			bool hasOriginalVolume = false;

			try
			{
				try
				{
					WaitDreqStableHigh( 3, 1000 );
					results.Add( new HealthTestResult( HealthTest.DreqReady, HealthCode.Success, "DREQ reached stable HIGH." ) );
				}
				catch( Exception ex )
				{
					results.Add( new HealthTestResult( HealthTest.DreqReady, HealthCode.DreqNotReady, ex.Message ) );
				}

				try
				{
					ushort mode = SciRead( Register.Mode );
					results.Add( new HealthTestResult( HealthTest.SciModeRead, HealthCode.Success, $"SCI_MODE=0x{mode:X4}" ) );
				}
				catch( Exception ex )
				{
					results.Add( new HealthTestResult( HealthTest.SciModeRead, HealthCode.SciModeReadFailed, ex.Message ) );
				}

				try
				{
					originalVolume = SciRead( Register.Volume );
					hasOriginalVolume = true;

					ushort testVolume = ( ushort )( originalVolume ^ 0x0F0F );
					if( testVolume == originalVolume )
					{
						testVolume = ( ushort )( originalVolume ^ 0x00FF );
					}

					SciWrite( Register.Volume, testVolume );

					ushort readBack = 0;
					bool matched = false;

					for( int attempt = 0; attempt < 3; attempt++ )
					{
						if( attempt > 0 )
						{
							Thread.Sleep( 2 );
						}

						readBack = SciRead( Register.Volume );
						if( readBack == testVolume )
						{
							matched = true;
							break;
						}
					}

					if( matched )
					{
						results.Add( new HealthTestResult( HealthTest.SciVolumeReadWrite, HealthCode.Success, $"SCI_VOL readback matched 0x{readBack:X4}." ) );
					}
					else
					{
						results.Add( new HealthTestResult( HealthTest.SciVolumeReadWrite, HealthCode.SciVolumeReadbackMismatch, $"SCI_VOL readback mismatch after retries. Wrote 0x{testVolume:X4}, last read 0x{readBack:X4}." ) );
					}
				}
				catch( Exception ex )
				{
					results.Add( new HealthTestResult( HealthTest.SciVolumeReadWrite, HealthCode.SciVolumeReadWriteFailed, ex.Message ) );
				}
				finally
				{
					if( hasOriginalVolume )
					{
						try
						{
							SciWriteWithRecovery( Register.Volume, originalVolume );
							results.Add( new HealthTestResult( HealthTest.SciVolumeRestore, HealthCode.Success, $"SCI_VOL restored to 0x{originalVolume:X4}." ) );
						}
						catch( Exception ex )
						{
							results.Add( new HealthTestResult( HealthTest.SciVolumeRestore, HealthCode.SciVolumeRestoreFailed, ex.Message ) );
						}
					}
					else
					{
						results.Add( new HealthTestResult( HealthTest.SciVolumeRestore, HealthCode.Skipped, "SCI_VOL restore skipped (original value unavailable)." ) );
					}
				}

				try
				{
					ushort decodeTime = SciRead( Register.DecodeTime );
					results.Add( new HealthTestResult( HealthTest.DecodeTimeRead, HealthCode.Success, $"SCI_DECODE_TIME=0x{decodeTime:X4}" ) );
				}
				catch( Exception ex )
				{
					results.Add( new HealthTestResult( HealthTest.DecodeTimeRead, HealthCode.DecodeTimeReadFailed, ex.Message ) );
				}

				if( includeSineTest )
				{
					try
					{
						StartSineTest();
						Thread.Sleep( sineDurationMs );
						StopSineTest();
						results.Add( new HealthTestResult( HealthTest.SineStartStop, HealthCode.Success, "Sine test start/stop sequence completed." ) );
					}
					catch( Exception ex )
					{
						results.Add( new HealthTestResult( HealthTest.SineStartStop, HealthCode.SineTestFailed, ex.Message ) );
					}
				}
				else
				{
					results.Add( new HealthTestResult( HealthTest.SineStartStop, HealthCode.Skipped, "Sine test skipped by caller." ) );
				}
			}
			catch( Exception ex )
			{
				results.Add( new HealthTestResult( HealthTest.SineStartStop, HealthCode.UnexpectedError, ex.Message ) );
			}

			return new HealthCheckReport( results.ToArray() );
		}
	}
}
