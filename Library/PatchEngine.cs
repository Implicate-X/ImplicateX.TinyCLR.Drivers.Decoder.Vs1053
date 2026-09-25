using System;
using System.Diagnostics;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public partial class Device
	{
		/// <summary>
		/// Provides functionality to load and apply plugin patches to the VS1053B audio decoder.
		/// </summary>
		/// <param name="device"></param>
		public sealed class PatchEngine( Device device ) : IDisposable
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
				byte[] pluginBytes = GetPluginBytesByType( patchType );

				if( pluginBytes == null || pluginBytes.Length == 0 )
				{
					throw new NotSupportedException( $"Patch type '{patchType}' is not available or not implemented." );
				}

				Debug.WriteLineIf( EnableVerbosePatchTrace, $"[PatchEngine] Loading {patchType}: {pluginBytes.Length} bytes" );

				// Convert binary resource (little-endian ushort[]) to ushort array
				ushort[] pluginData = ConvertBinaryToUshortArray( pluginBytes );

				Debug.WriteLineIf( EnableVerbosePatchTrace, $"[PatchEngine] Plugin '{patchType}' loaded: {pluginData.Length} words" );

				ApplyPluginData( pluginData );
			}

			/// <summary>
			/// Loads a plugin patch from provided plugin data array.
			/// </summary>
			/// <param name="pluginData">The plugin data array in RLE-compressed VS1053 format.</param>
			public void LoadPlugin( ushort[] pluginData )
			{
				if( pluginData == null || pluginData.Length == 0 )
				{
					throw new ArgumentException( "Plugin data cannot be null or empty", nameof( pluginData ) );
				}

				ApplyPluginData( pluginData );
			}

			/// <summary>
			/// Applies plugin data to the VS1053B by parsing RLE-compressed format and writing registers.
			/// The .plg format uses run-length encoding:
			///   addr (ushort)
			///   n (ushort) - high bit (0x8000) indicates RLE vs literal
			///   if RLE: value (ushort) - write this value n times
			///   if literal: value[0..n-1] (n ushorts) - write each value sequentially
			/// </summary>
			private void ApplyPluginData( ushort[] pluginData )
			{
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
			/// Retrieves the binary plugin data for a given patch type from embedded resources.
			/// </summary>
			/// <param name="patchType">The type of patch for which to retrieve binary data.</param>
			/// <returns>A byte array containing the binary plugin data.</returns>
			/// <exception cref="NotSupportedException">Thrown when the specified patch type is not embedded as a resource.</exception>
			private byte[] GetPluginBytesByType( PatchType patchType )
			{
				return patchType switch
				{
					PatchType.StandardCodec => Plugins.GetBytes( Plugins.BinaryResources.Standard ),
					PatchType.Dsd => Plugins.GetBytes( Plugins.BinaryResources.Dsd ),
					PatchType.Flac => Plugins.GetBytes( Plugins.BinaryResources.Flac ),
					PatchType.Latm => Plugins.GetBytes( Plugins.BinaryResources.Latm ),
					PatchType.FlacLatm => Plugins.GetBytes( Plugins.BinaryResources.FlacLatm ),
					PatchType.Pitch => Plugins.GetBytes( Plugins.BinaryResources.Pitch ),
					_ => throw new NotSupportedException( $"Patch type '{patchType}' is currently not embedded as resource." ),
				};
			}


			/// <summary>
			/// Converts a byte array of binary plugin data into an array of ushorts, interpreting the bytes as little-endian pairs.
			/// </summary>
			/// <param name="binaryData">The byte array containing binary plugin data.</param>
			/// <returns>An array of ushorts representing the binary plugin data.</returns>
			/// <exception cref="InvalidOperationException">Thrown when the binary data length is not even.</exception>
			private static ushort[] ConvertBinaryToUshortArray( byte[] binaryData )
			{
				if( binaryData == null || binaryData.Length == 0 )
				{
					return new ushort[ 0 ];
				}

				if( binaryData.Length % 2 != 0 )
				{
					throw new InvalidOperationException( "Binary plugin data must have even length (each ushort = 2 bytes)." );
				}

				ushort[] result = new ushort[ binaryData.Length / 2 ];

				for( int i = 0; i < result.Length; i++ )
				{
					// Read little-endian ushort
					result[ i ] = ( ushort )( binaryData[ i * 2 ] | ( binaryData[ i * 2 + 1 ] << 8 ) );
				}

				return result;
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
