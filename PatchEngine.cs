using System;
using System.Diagnostics;
using ImplicateX.TinyCLR.Drivers.Decoder.Patch;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	public partial class Vs1053
	{
		/// <summary>
		/// Provides functionality to load and apply plugin patches to the VS1053B audio decoder.
		/// </summary>
		/// <param name="device"></param>
		public sealed class PatchEngine( Vs1053 device ) : IDisposable
		{
			private const bool EnableVerbosePatchTrace = false;

			/// <summary>
			/// Enumeration of available patch types supported by the VS1053B.
			/// </summary>
			public enum PatchType
			{
				/// <summary>Standard codec patches for Vorbis, MP3, etc.</summary>
				StandardCodec,
				/// <summary>DSD (Direct Stream Digital) codec support.</summary>
				Dsd,
				/// <summary>FLAC codec support.</summary>
				Flac,
				/// <summary>LATM/AAC codec support.</summary>
				Latm,
				/// <summary>Combined FLAC + LATM/AAC codec support.</summary>
				FlacLatm,
				/// <summary>Pitch control patch.</summary>
				Pitch
			}

			/// <summary>
			/// Loads the default plugin patch (StandardCodec) into the VS1053B.
			/// This loads the resource-based binary patch data.
			/// </summary>
			public void LoadPlugin()
			{
				LoadPlugin( PatchType.StandardCodec );
			}

			/// <summary>
			/// Loads a specific patch type into the VS1053B.
			/// </summary>
			/// <param name="patchType">The type of patch to load.</param>
			public void LoadPlugin( PatchType patchType )
			{
				ushort[] pluginData = patchType switch
				{
					PatchType.StandardCodec => Standard.Data,
					PatchType.Dsd => Dsd.Data,
					PatchType.Flac => Flac.Data,
					PatchType.Latm => Latm.Data,
					PatchType.FlacLatm => FlacLatm.Data,
					PatchType.Pitch => Pitch.Data,
					_ => throw new NotSupportedException( $"Patch type '{patchType}' is currently not embedded as resource." ),
				};

				Debug.WriteLineIf( EnableVerbosePatchTrace, $"[PatchEngine] ApplyPluginData: Starting with {pluginData.Length} ushorts" );

				int index = 0;
				int writeCount = 0;
				int runCount = 0;

				while( index < pluginData.Length )
				{
					if( index + 1 >= pluginData.Length )
					{
						// Not enough data for addr + n
						break;
					}

					ushort address = pluginData[index];
					ushort n = pluginData[index + 1];
					index += 2;
					runCount++;

					if( ( n & 0x8000U ) != 0 )
					{
						// RLE run: replicate one value n times
						if( index >= pluginData.Length )
						{
							break;
						}

						ushort value = pluginData[index++];
						int count = n & 0x7FFF;

						Debug.WriteLineIf( EnableVerbosePatchTrace, $"  [RLE] Addr=0x{address:X2}, Count={count}, Value=0x{value:X4}" );

						for( int i = 0; i < count; i++ )
						{
							device.SciWrite( (Register)address, value );
							writeCount++;
						}
					}
					else
					{
						// Literal run: copy n values sequentially
						Debug.WriteLineIf( EnableVerbosePatchTrace, $"  [LITERAL] Addr=0x{address:X2}, Count={n}" );

						for( int i = 0; i < n; i++ )
						{
							if( index >= pluginData.Length )
							{
								Debug.WriteLineIf( EnableVerbosePatchTrace, $"    [ERROR] Ran out of data at run {runCount}, literal item {i}/{n}" );
								break;
							}

							ushort value = pluginData[index];
							index++;

							// Only log critical registers (0x0A = SCI_APPLICATIONS)
							Debug.WriteLineIf( EnableVerbosePatchTrace, $"    [CRITICAL] Writing 0x{address:X2} = 0x{value:X4}" );

							device.SciWrite( (Register)address, value );
							writeCount++;
						}
					}
				}

				Debug.WriteLineIf( EnableVerbosePatchTrace, $"[PatchEngine] Applied {runCount} runs, {writeCount} total register writes" );
				device.AwaitPatchReady();
				Debug.WriteLineIf( EnableVerbosePatchTrace, "[PatchEngine] Patch restart complete (DREQ ready)." );
			}

			/// <summary>
			/// Disposes of the PatchEngine and releases any resources if necessary.
			/// </summary>
			public void Dispose()
			{
				// Cleanup resources if needed
			}
		}
	}
}
