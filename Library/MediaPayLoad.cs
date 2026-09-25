using System;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Represents a media payload and playback configuration for the VS1053 decoder.
	/// </summary>
	/// <param name="stream">The source stream containing the media data to play.</param>
	/// <param name="fillerBytes">The number of filler bytes to append after the stream content.</param>
	/// <param name="requiresStartupMode">
	/// Indicates whether the decoder must be placed into startup mode before playback begins.
	/// </param>
	/// <param name="customClockFrequency">
	/// Optional custom clock frequency in Hz. Use <c>0</c> to keep the default clock configuration.
	/// </param>
	/// <param name="startFillByte">
	/// The byte value used to fill the startup buffer before streaming begins.
	/// </param>
	/// <param name="patchType">
	/// The codec patch that should be loaded before playback starts.
	/// </param>
	public sealed class MediaPayLoad
	(
		Stream stream,
		int fillerBytes,
		bool requiresStartupMode = true,
		int customClockFrequency = 0,
		byte startFillByte = 0x00,
		Device.PatchEngine.PatchType patchType = Device.PatchEngine.PatchType.StandardCodec
	)
	{
		/// <summary>
		/// Gets the source stream containing the media data.
		/// </summary>
		public Stream Stream { get; } = stream;

		/// <summary>
		/// Gets the number of filler bytes to append after playback data.
		/// </summary>
		public int FillerBytes { get; } = fillerBytes;

		/// <summary>
		/// Gets a value indicating whether startup mode is required before playback.
		/// </summary>
		public bool RequiresStartupMode { get; } = requiresStartupMode;

		/// <summary>
		/// Gets the custom clock frequency in Hz, or <c>0</c> when not specified.
		/// </summary>
		public int CustomClockFrequency { get; } = customClockFrequency;

		/// <summary>
		/// Gets the byte value used to fill the startup buffer.
		/// </summary>
		public byte StartFillByte { get; } = startFillByte;

		/// <summary>
		/// Gets the patch type required for decoder playback.
		/// </summary>
		public Device.PatchEngine.PatchType PatchType { get; } = patchType;
	}
}