using System;
using System.Collections.Generic;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	/// <summary>
	/// A registry for media preprocessors that can handle different audio file formats.
	/// </summary>
	public static class PreprocessorRegistry
	{
		/// <summary>
		/// A list of available media preprocessors for different audio file formats.
		/// </summary>
		private static readonly List<IMediaPreprocessor> processors =
		[
			new Mp3Processor(),
			new WavProcessor(),
			new AacProcessor(),
			new FlacProcessor(),
			new OggProcessor(),
			new DsdProcessor(),
			new M4aProcessor()
		];

		/// <summary>
		/// Resolves the appropriate media preprocessor for a given file extension.
		/// </summary>
		/// <param name="extension">The file extension to resolve the preprocessor for.</param>
		/// <returns>The media preprocessor that can handle the specified file extension.</returns>
		/// <exception cref="NotSupportedException">Thrown if no processor is found for the specified extension.</exception>
		public static IMediaPreprocessor Resolve( string extension )
		{
			foreach( var p in processors )
				if( p.CanProcess( extension ) )
					return p;

			throw new NotSupportedException( $"No processor found for extension {extension}" );
		}
	}
}
