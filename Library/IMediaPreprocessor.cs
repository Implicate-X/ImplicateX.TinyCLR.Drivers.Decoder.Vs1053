using System;
using System.IO;

namespace ImplicateX.TinyCLR.Drivers.Decoder.Vs1053
{
	public interface IMediaPreprocessor
	{
		bool CanProcess( string extension );
		MediaPayLoad Process( FileStream fs );
	}
}
