using System;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// Defines a preprocessor that prepares a media stream for VS1053 playback.
	/// </summary>
	public interface IMediaPreprocessor
	{
		/// <summary>
		/// Determines whether this preprocessor can handle the specified file extension.
		/// </summary>
		/// <param name="extension">The file extension, including the leading period (for example, <c>.mp3</c>).</param>
		/// <returns>
		/// <see langword="true" /> if the extension is supported; otherwise, <see langword="false" />.
		/// </returns>
		bool CanProcess( string extension );

		/// <summary>
		/// Prepares the source stream for playback and returns the payload required by the decoder.
		/// </summary>
		/// <param name="fs">The source media file stream.</param>
		/// <returns>A <see cref="MediaPayLoad" /> describing the stream and decoder setup.</returns>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="fs" /> is <see langword="null" />.</exception>
		MediaPayLoad Process( FileStream fs );
	}
}